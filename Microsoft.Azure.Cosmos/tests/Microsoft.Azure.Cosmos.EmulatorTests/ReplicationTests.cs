﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.SDK.EmulatorTests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Collections;
    using Microsoft.Azure.Cosmos.Internal;
    using Microsoft.Azure.Cosmos.Linq;
    using Microsoft.Azure.Cosmos.Utils;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public sealed class ReplicationTests
    {
        private readonly int serverStalenessIntervalInSeconds;
        private readonly int masterStalenessIntervalInSeconds;

        internal static readonly int ConfigurationRefreshIntervalInSec = 40;
        internal static readonly int BackendConfigurationRefreshIntervalInSec = 240;

        public ReplicationTests()
        {
            this.serverStalenessIntervalInSeconds = int.Parse(ConfigurationManager.AppSettings["ServerStalenessIntervalInSeconds"], CultureInfo.InvariantCulture);
            this.masterStalenessIntervalInSeconds = int.Parse(ConfigurationManager.AppSettings["MasterStalenessIntervalInSeconds"], CultureInfo.InvariantCulture);
        }


        private void WaitForMasterReplication()
        {
            if (this.masterStalenessIntervalInSeconds != 0)
            {
                Task.Delay(this.masterStalenessIntervalInSeconds * 1000);
            }
        }

        private void WaitForServerReplication()
        {
            if (this.serverStalenessIntervalInSeconds != 0)
            {
                Task.Delay(this.serverStalenessIntervalInSeconds * 1000);
            }
        }
#region Replication Validation Helpers
        internal static bool ResourceDoesnotExists<T>(string resourceId,
            DocumentClient client,
            string ownerId = null) where T : CosmosResource, new()
        {
            T nonExistingResource;
            try
            {
                if (typeof(T) != typeof(Attachment))
                {
                    INameValueCollection responseHeaders;
                    nonExistingResource = TestCommon.ReadWithRetry<T>(client, resourceId, out responseHeaders);
                }
                else
                {
                    Attachment nonExisitingAttachment = client.Read<Attachment>(resourceId);
                }
                return false;
            }
            catch (DocumentClientException clientException)
            {
                TestCommon.AssertException(clientException, HttpStatusCode.NotFound);
                return true;
            }
        }

        internal static void ValidateCollection<T>(DocumentClient[] replicaClients,
            string collectionId,
            INameValueCollection headers = null,
            bool verifyAddress = true) where T : CosmosResource, new()
        {
            Assert.IsTrue(replicaClients != null && replicaClients.Length > 1, "Must pass in at least two replica clients");

            Task.Delay(3000); // allow previous operations to complete and propagate, bringing more robustness to tests

            foreach (DocumentClient client in replicaClients)
            {
                client.ForceAddressRefresh(true);
                if (typeof(T) == typeof(CosmosStoredProcedureSettings) ||
                    typeof(T) == typeof(CosmosUserDefinedFunctionSettings) ||
                    typeof(T) == typeof(CosmosTriggerSettings))
                {
                    TestCommon.ListAllScriptDirect<T>(client, collectionId, headers ?? new StringKeyValueCollection());
                }
                else
                {
                    TestCommon.ListAll<T>(client, collectionId, headers ?? new StringKeyValueCollection(), true);
                }
            }

            try
            {
                BackoffRetryUtility<bool>.ExecuteAsync((bool isInRetry) =>
                {
                    var feeds = new FeedResponse<T>[replicaClients.Length];
                    var allHeaders = new StringKeyValueCollection[replicaClients.Length];
                    for (int i = 0; i < replicaClients.Length; i++)
                    {
                        var header = new StringKeyValueCollection();
                        if (headers != null)
                        {
                            foreach (string key in headers)
                            {
                                header[key] = headers[key];
                            }
                        }

                        allHeaders[i] = header;
                    }

                    var continuations = new string[replicaClients.Length];
                    var responseHeaders = new INameValueCollection[replicaClients.Length];

                    do
                    {
                        for (int i = 0; i < replicaClients.Length; i++)
                        {
                            if (!string.IsNullOrEmpty(continuations[i]))
                            {
                                allHeaders[i][HttpConstants.HttpHeaders.Continuation] = continuations[i];
                            }

                            feeds[i] = replicaClients[i].ReadFeedWithRetry<T>(collectionId, out responseHeaders[i],
                                allHeaders[i]);

                            if (responseHeaders[i] != null)
                            {
                                continuations[i] = responseHeaders[i][HttpConstants.HttpHeaders.Continuation];
                            }
                            else
                            {
                                continuations[i] = null;
                            }
                        }

                        for (int i = 0; i < replicaClients.Length - 1; i++)
                        {
                            for (int j = i + 1; j < replicaClients.Length; j++)
                            {
                                Assert.AreEqual(continuations[i], continuations[j], "Collection Continuaton mismatch");
                                if (verifyAddress)
                                {
                                    var address1 = replicaClients[i].GetAddress();
                                    var address2 = replicaClients[j].GetAddress();

                                    // If the addresses match, we are in mid of reconfiguration, throw GoneException so that we retry on the entire loop
                                    // and take care of intermittent gone at the same time.
                                    if (address1.Equals(address2))
                                    {
                                        throw new GoneException("Addresses matched for multiple replicas " + address1);
                                    }
                                }
                            }
                        }

                        ReplicationTests.ValidateCollection<T>(feeds);

                    } while (!string.IsNullOrEmpty(continuations[0]));

                    return Task.FromResult<bool>(true);
                }, new GoneAndRetryWithRetryPolicy()).Wait();
            }
            finally
            {
                //Once address are stable. dont force the cache refresh.
                foreach (DocumentClient client in replicaClients)
                {
                    client.ForceAddressRefresh(false);
                }
            }
        }

        private static void ValidateCollection<T>(FeedResponse<T>[] collectionResponses) where T : CosmosResource, new()
        {
            for (int i = 0; i < collectionResponses.Length - 1; i++)
            {
                for (int j = i + 1; j < collectionResponses.Length; j++)
                {
                    Assert.AreEqual(collectionResponses[i].ResponseContinuation, collectionResponses[j].ResponseContinuation, "Continuation(s) between collection response mismatch");
                    Assert.AreEqual(collectionResponses[i].Count, collectionResponses[j].Count, "Count between collection response mismatch");
                }
            }

            for (int i = 0; i < collectionResponses[0].Count; ++i)
            {
                List<T> resources = collectionResponses.Select(t => t.ElementAt(i)).ToList();
                ReplicationTests.ValidateResourceProperties(resources);
            }
        }

        internal static void ValidateResourceProperties<T>(List<T> resources) where T : CosmosResource, new()
        {
            for (int i = 0; i < resources.Count - 1; i++)
            {
                for (int j = i + 1; j < resources.Count; j++)
                {
                    // First validate resource properties
                    Assert.AreEqual(resources[i].ResourceId, resources[j].ResourceId, "RID mismatched");
                    Assert.AreEqual(resources[i].ETag, resources[j].ETag, "ETag mismatched");
                    Assert.AreEqual(resources[i].Id, resources[j].Id, "Name mismatched");
                    Assert.AreEqual(resources[i].SelfLink, resources[j].SelfLink, "SelfLink mismatched");
                    Assert.AreEqual(resources[i].Timestamp, resources[j].Timestamp, "Timestamp mismatched");
                }
            }

            if (typeof(T) == typeof(CosmosDatabaseSettings))
            {
                var databases = resources.Cast<CosmosDatabaseSettings>().ToArray();

                for (int i = 0; i < resources.Count - 1; i++)
                {
                    for (int j = i + 1; j < resources.Count; j++)
                    {
                        Assert.AreEqual(databases[i].CollectionsLink, databases[j].CollectionsLink,
                            "Database CollectionLink don't match");
                    }
                }
            }
            else if (typeof(T) == typeof(CosmosContainerSettings))
            {
                var documentCollections = resources.Cast<CosmosContainerSettings>().ToArray();

                for (int i = 0; i < documentCollections.Length - 1; i++)
                {
                    for (int j = i + 1; j < documentCollections.Length; j++)
                    {
                        Assert.AreEqual(documentCollections[i].DocumentsLink,
                            documentCollections[j].DocumentsLink,
                            "DocumentCollection DocumentsLink mismatch");
                        Assert.AreEqual(documentCollections[i].IndexingPolicy.Automatic,
                            documentCollections[j].IndexingPolicy.Automatic,
                            "DocumentCollection IndexingPolicy.Automatic mismatch");
                        Assert.AreEqual(documentCollections[i].IndexingPolicy.IndexingMode,
                            documentCollections[j].IndexingPolicy.IndexingMode,
                            "DocumentCollection IndexingPolicy.IndexingMode mismatch");
                        // TODO: nemanjam, add other collection properties
                    }
                }
            }
            else if (typeof(T) == typeof(User))
            {
                var users = resources.Cast<User>().ToArray();

                for (int i = 0; i < users.Length - 1; i++)
                {
                    for (int j = i + 1; j < users.Length; j++)
                    {
                        Assert.AreEqual(users[i].PermissionsLink, users[j].PermissionsLink,
                            "User PermissionsLink mismatch");
                    }
                }
            }
            else if (typeof(T) == typeof(Permission))
            {
                var permissions = resources.Cast<Permission>().ToArray();

                for (int i = 0; i < permissions.Length - 1; i++)
                {
                    for (int j = i + 1; j < permissions.Length; j++)
                    {
                        Assert.AreEqual(permissions[i].PermissionMode, permissions[j].PermissionMode,
                            "Permission PermissionMode mismatch");
                        Assert.AreEqual(permissions[i].ResourceLink, permissions[j].ResourceLink,
                            "Permission ResourceLink mismatch");
                    }
                }
            }
            else if (typeof(T) == typeof(Document))
            {
                var documents = resources.Cast<Document>().ToArray();

                for (int i = 0; i < documents.Length - 1; i++)
                {
                    for (int j = i + 1; j < documents.Length; j++)
                    {
                        Assert.AreEqual(documents[i].AttachmentsLink, documents[j].AttachmentsLink,
                            "Document AttachmentsLink mismatch");
                    }
                }

                //TODO, KRAMAN, ADD validation for document content.
            }
            else if (typeof(T) == typeof(Attachment))
            {
                var attachments = resources.Cast<Attachment>().ToArray();

                for (int i = 0; i < attachments.Length - 1; i++)
                {
                    for (int j = i + 1; j < attachments.Length; j++)
                    {
                        Assert.AreEqual(attachments[i].ContentType, attachments[j].ContentType,
                            "Attachment ContentType mismatch");
                        Assert.AreEqual(attachments[i].MediaLink, attachments[j].MediaLink,
                            "Attachment MediaLink mismatch");
                    }
                }
            }
            else if (typeof(T) == typeof(CosmosStoredProcedureSettings))
            {
                var storedProcedures = resources.Cast<CosmosStoredProcedureSettings>().ToArray();

                for (int i = 0; i < storedProcedures.Length - 1; i++)
                {
                    for (int j = i + 1; j < storedProcedures.Length; j++)
                    {
                        Assert.AreEqual(storedProcedures[i].Body, storedProcedures[j].Body,
                            "StoredProcedure Body mismatch");
                    }
                }
            }
            else if (typeof(T) == typeof(CosmosTriggerSettings))
            {
                var triggers = resources.Cast<CosmosTriggerSettings>().ToArray();

                for (int i = 0; i < triggers.Length - 1; i++)
                {
                    for (int j = i + 1; j < triggers.Length; j++)
                    {
                        Assert.AreEqual(triggers[i].Body, triggers[j].Body, "Trigger Body mismatch");
                    }
                }
            }
            else if (typeof(T) == typeof(CosmosUserDefinedFunctionSettings))
            {
                var userDefinedFunctions = resources.Cast<CosmosUserDefinedFunctionSettings>().ToArray();

                for (int i = 0; i < userDefinedFunctions.Length - 1; i++)
                {
                    for (int j = i + 1; j < userDefinedFunctions.Length; j++)
                    {
                        Assert.AreEqual(userDefinedFunctions[i].Body, userDefinedFunctions[j].Body,
                            "UserDefinedFunction Body mismatch");
                    }
                }
            }
            else if (typeof(T) == typeof(Conflict))
            {
                var conflicts = resources.Cast<Conflict>().ToArray();

                for (int i = 0; i < conflicts.Length - 1; i++)
                {
                    for (int j = i + 1; j < conflicts.Length; j++)
                    {
                        Assert.AreEqual(conflicts[i].ResourceType, conflicts[j].ResourceType,
                            "Conflict ResourceType mismatch");
                        Assert.AreEqual(conflicts[i].OperationKind, conflicts[j].OperationKind,
                            "Conflict OperationKind mismatch");
                        Assert.AreEqual(conflicts[i].ResourceId, conflicts[j].ResourceId,
                            "Conflict ResourceId mismatch");
                        Assert.AreEqual(conflicts[i].OperationKind, conflicts[j].OperationKind,
                            "Conflict OperationKind mismatch");

                        if (conflicts[i].OperationKind == OperationKind.Delete)
                        {
                            continue;
                        }

                        if (conflicts[i].ResourceType == typeof(Attachment))
                        {
                            ReplicationTests.ValidateResourceProperties<Attachment>(
                                conflicts.Select(x => x.GetResource<Attachment>()).ToList());
                        }
                        else if (conflicts[i].ResourceType == typeof(Document))
                        {
                            ReplicationTests.ValidateResourceProperties<Document>(
                                conflicts.Select(x => x.GetResource<Document>()).ToList());
                        }
                        else if (conflicts[i].ResourceType == typeof(CosmosStoredProcedureSettings))
                        {
                            ReplicationTests.ValidateResourceProperties<CosmosStoredProcedureSettings>(
                                conflicts.Select(x => x.GetResource<CosmosStoredProcedureSettings>()).ToList());
                        }
                        else if (conflicts[i].ResourceType == typeof(CosmosTriggerSettings))
                        {
                            ReplicationTests.ValidateResourceProperties<CosmosTriggerSettings>(
                                conflicts.Select(x => x.GetResource<CosmosTriggerSettings>()).ToList());
                        }
                        else if (conflicts[i].ResourceType == typeof(CosmosUserDefinedFunctionSettings))
                        {
                            ReplicationTests.ValidateResourceProperties<CosmosUserDefinedFunctionSettings>(
                                conflicts.Select(x => x.GetResource<CosmosUserDefinedFunctionSettings>()).ToList());
                        }
                        else
                        {
                            Assert.Fail("Invalid resource type {0}", conflicts[i].ResourceType);
                        }
                    }
                }
            }
        }

        internal static void ValidateQuery<T>(DocumentClient client, string collectionLink, string queryProperty, string queryPropertyValue, int expectedCount, INameValueCollection headers = null)
            where T : CosmosResource, new()
        {
            if (headers != null)
                headers = new StringKeyValueCollection(headers); // dont mess with the input headers
            else
                headers = new StringKeyValueCollection();

            int maxTries = 5;
            const int minIndexInterval = 5000; // 5 seconds
            while (maxTries-- > 0)
            {
                FeedResponse<dynamic> resourceFeed = null;
                IDocumentQuery<dynamic> queryService = null;
                string queryString = @"select * from root r where r." + queryProperty + @"=""" + queryPropertyValue + @"""";
                if (typeof(T) == typeof(CosmosDatabaseSettings))
                {
                    queryService = client.CreateDatabaseQuery(queryString).AsDocumentQuery();
                }
                else if (typeof(T) == typeof(CosmosContainerSettings))
                {
                    queryService = client.CreateDocumentCollectionQuery(collectionLink, queryString).AsDocumentQuery();
                }
                else if (typeof(T) == typeof(Document))
                {
                    queryService = client.CreateDocumentQuery(collectionLink, queryString).AsDocumentQuery();
                }
                else
                {
                    Assert.Fail("Unexpected type");
                }

                while (queryService.HasMoreResults)
                {
                    resourceFeed = queryService.ExecuteNextAsync().Result;

                    if (resourceFeed.Count > 0)
                    {
                        Assert.IsNotNull(resourceFeed, "Query result is null");
                        Assert.AreNotEqual(0, resourceFeed.Count, "Query result is invalid");

                        foreach (T resource in resourceFeed)
                        {
                            if (queryProperty.Equals("name", StringComparison.CurrentCultureIgnoreCase))
                                Assert.AreEqual(resource.Id, queryPropertyValue, "Result contain invalid result");
                        }
                        return;
                    }
                }

                Task.Delay(minIndexInterval);
            }

            Assert.Fail("Query did not return result after max tries");
        }

        ///// <summary>
        ///// Changes the replication mode to sync for server.
        ///// </summary>
        ///// <param name="fabricClient"></param>
        //internal static void SwitchServerToSyncReplication(COMMONNAMESPACE.IFabricClient fabricClient, string callingComponent)
        //{
        //    if (!IsCurrentReplicationModeAsync(fabricClient)["Server"])
        //    {
        //        return;
        //    }

        //    NamingServiceConfig.NamingServiceConfigurationWriter namingServiceWriter =
        //            new NamingServiceConfig.NamingServiceConfigurationWriter(fabricClient);

        //    NamingServiceConfig.DocumentServiceConfiguration config = new NamingServiceConfig.DocumentServiceConfiguration();
        //    config.DocumentServiceName = ConfigurationManager.AppSettings["DatabaseAccountId"];
        //    config.IsServerReplicationAsync = false;

        //    namingServiceWriter.UpdateDatabaseAccountConfigurationAsync(config).Wait();

        //    Task.Delay(TimeSpan.FromSeconds(ReplicationTests.ConfigurationRefreshIntervalInSec)).Wait();
        //    TestCommon.ForceRefreshNamingServiceConfigs(callingComponent, FabricServiceType.ServerService).Wait();
        //}

        ///// <summary>
        ///// Changes the replication mode to async for server.
        ///// </summary>
        ///// <param name="fabricClient"></param>
        //internal static void SwitchServerToAsyncReplication(COMMONNAMESPACE.IFabricClient fabricClient, string callingComponent)
        //{
        //    if (IsCurrentReplicationModeAsync(fabricClient)["Server"])
        //    {
        //        return;
        //    }

        //    NamingServiceConfig.NamingServiceConfigurationWriter namingServiceWriter =
        //        new NamingServiceConfig.NamingServiceConfigurationWriter(fabricClient);

        //    NamingServiceConfig.DocumentServiceConfiguration config = new NamingServiceConfig.DocumentServiceConfiguration();
        //    config.DocumentServiceName = ConfigurationManager.AppSettings["DatabaseAccountId"];
        //    config.IsServerReplicationAsync = true;

        //    namingServiceWriter.UpdateDatabaseAccountConfigurationAsync(config).Wait();

        //    Task.Delay(TimeSpan.FromSeconds(ReplicationTests.ConfigurationRefreshIntervalInSec)).Wait();
        //    TestCommon.ForceRefreshNamingServiceConfigs(callingComponent, FabricServiceType.ServerService).Wait();
        //}

#endregion

#region Environment Configuration Helpers

        internal static DocumentClient[] GetClientsLocked(bool useGateway = false, Protocol protocol = Protocol.Tcp, int timeoutInSeconds = 10, ConsistencyLevel? defaultConsistencyLevel = null, AuthorizationTokenType tokenType = AuthorizationTokenType.PrimaryMasterKey)
        {
            var toReturn = new DocumentClient[TestCommon.ReplicationFactor];
            for (uint i = 0; i < toReturn.Length; i++)
            {
                toReturn[i] = TestCommon.CreateClient(useGateway, protocol, timeoutInSeconds, defaultConsistencyLevel, tokenType);
                toReturn[i].LockClient(i);
            }

            return toReturn;
        }
#endregion
    }
}
