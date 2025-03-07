﻿namespace Microsoft.Azure.Cosmos.SDK.EmulatorTests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.FaultInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using static Microsoft.Azure.Cosmos.SDK.EmulatorTests.MultiRegionSetupHelpers;

    [TestClass]
    public class CosmosItemIntegrationTests
    {
        private string connectionString;
        private CosmosClient client;
        private Database database;
        private Container container;
        private Container changeFeedContainer;

        private static string region1;
        private static string region2;
        private static string region3;
        private CosmosSystemTextJsonSerializer cosmosSystemTextJsonSerializer;

        [TestInitialize]
        public async Task TestInitAsync()
        {
            this.connectionString = ConfigurationManager.GetEnvironmentVariable<string>("COSMOSDB_MULTI_REGION", null);

            JsonSerializerOptions jsonSerializerOptions = new JsonSerializerOptions()
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            this.cosmosSystemTextJsonSerializer = new CosmosSystemTextJsonSerializer(jsonSerializerOptions);

            if (string.IsNullOrEmpty(this.connectionString))
            {
                Assert.Fail("Set environment variable COSMOSDB_MULTI_REGION to run the tests");
            }
            this.client = new CosmosClient(
                this.connectionString,
                new CosmosClientOptions()
                {
                    Serializer = this.cosmosSystemTextJsonSerializer,
                });

            (this.database, this.container, this.changeFeedContainer) = await MultiRegionSetupHelpers.GetOrCreateMultiRegionDatabaseAndContainers(this.client);

            IDictionary<string, Uri> readRegions = this.client.DocumentClient.GlobalEndpointManager.GetAvailableReadEndpointsByLocation();
            Assert.IsTrue(readRegions.Count() >= 3);

            region1 = readRegions.Keys.ElementAt(0);
            region2 = readRegions.Keys.ElementAt(1);
            region3 = readRegions.Keys.ElementAt(2);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                this.container.DeleteItemAsync<CosmosIntegrationTestObject>("deleteMe", new PartitionKey("MMWrite"));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Ignore
            }
            finally
            {
                //Do not delete the resources (except MM Write test object), georeplication is slow and we want to reuse the resources
                this.client?.Dispose();
            }
        }

        [TestMethod]
        [TestCategory("MultiRegion")]
        [Timeout(70000)]
        public async Task ReadMany2UnreachablePartitionsTest()
        {
            List<FeedRange> feedRanges = (List<FeedRange>)await this.container.GetFeedRangesAsync();
            Assert.IsTrue(feedRanges.Count > 0);

            FaultInjectionCondition condition = new FaultInjectionConditionBuilder()
                .WithConnectionType(FaultInjectionConnectionType.Direct)
                .WithOperationType(FaultInjectionOperationType.QueryItem)
                .WithEndpoint(new FaultInjectionEndpointBuilder(
                    MultiRegionSetupHelpers.dbName,
                    MultiRegionSetupHelpers.containerName,
                    feedRanges[0])
                    .WithReplicaCount(2)
                    .WithIncludePrimary(false)
                    .Build())
                .Build();

            FaultInjectionServerErrorResult result = new FaultInjectionServerErrorResultBuilder(FaultInjectionServerErrorType.Gone)
                .WithTimes(int.MaxValue - 1)
                .Build();

            FaultInjectionRule rule = new FaultInjectionRuleBuilder("connectionDelay", condition, result)
                .WithDuration(TimeSpan.FromDays(1))
                .Build();

            FaultInjector injector = new FaultInjector(new List<FaultInjectionRule> { rule });

            rule.Disable();

            CosmosClientOptions clientOptions = new CosmosClientOptions()
            {
                ConnectionMode = ConnectionMode.Direct,
                ConsistencyLevel = ConsistencyLevel.Strong,
                //Serializer = this.cosmosSystemTextJsonSerializer,
                FaultInjector = injector,
            };

            CosmosClient fiClient = new CosmosClient(
                connectionString: this.connectionString,
                clientOptions: clientOptions);

            Database fidb = fiClient.GetDatabase(MultiRegionSetupHelpers.dbName);
            Container fic = fidb.GetContainer(MultiRegionSetupHelpers.containerName);

            IReadOnlyList<(string, PartitionKey)> items = new List<(string, PartitionKey)>()
            {
                ("testId", new PartitionKey("pk")),
                ("testId2", new PartitionKey("pk2")),
                ("testId3", new PartitionKey("pk3")),
                ("testId4", new PartitionKey("pk4")),
            };

            try
            {
                rule.Enable();
                FeedResponse<CosmosIntegrationTestObject> feedResponse = await fic.ReadManyItemsAsync<CosmosIntegrationTestObject>(items);
            }
            catch (Exception ex)
            {
                Assert.Fail(ex.ToString());
            }
            finally
            {
                rule.Disable();
                fiClient.Dispose();
            }
        }

        [TestMethod]
        [Owner("dkunda")]
        [TestCategory("MultiRegion")]
        [DataRow(true, DisplayName = "Test scenario when binary encoding is enabled at client level.")]
        [DataRow(false, DisplayName = "Test scenario when binary encoding is disabled at client level.")]
        public async Task ExecuteTransactionalBatch_WhenBinaryEncodingEnabled_ShouldCompleteSuccessfully(
            bool isBinaryEncodingEnabled)
        {
            Environment.SetEnvironmentVariable(ConfigurationManager.BinaryEncodingEnabled, isBinaryEncodingEnabled.ToString());

            Random random = new();
            CosmosIntegrationTestObject testItem = new()
            {
                Id = $"smTestId{random.Next()}",
                Pk = $"smpk{random.Next()}",
            };

            try
            {
                CosmosClientOptions cosmosClientOptions = new()
                {
                    ConsistencyLevel = ConsistencyLevel.Session,
                    RequestTimeout = TimeSpan.FromSeconds(10),
                    Serializer = new CosmosJsonDotNetSerializer(
                        cosmosSerializerOptions: new CosmosSerializationOptions()
                        {
                            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                        },
                        binaryEncodingEnabled: isBinaryEncodingEnabled)
                };

                using CosmosClient cosmosClient = new(
                    connectionString: this.connectionString,
                    clientOptions: cosmosClientOptions);

                Database database = cosmosClient.GetDatabase(MultiRegionSetupHelpers.dbName);
                Container container = database.GetContainer(MultiRegionSetupHelpers.containerName);

                // Create a transactional batch
                TransactionalBatch transactionalBatch = container.CreateTransactionalBatch(new PartitionKey(testItem.Pk));

                transactionalBatch.CreateItem(
                    testItem,
                    new TransactionalBatchItemRequestOptions
                    {
                        EnableContentResponseOnWrite = true,
                    });

                transactionalBatch.ReadItem(
                    testItem.Id,
                    new TransactionalBatchItemRequestOptions
                    {
                        EnableContentResponseOnWrite = true,
                    });

                // Execute the transactional batch
                TransactionalBatchResponse transactionResponse = await transactionalBatch.ExecuteAsync(
                    new TransactionalBatchRequestOptions
                    {
                    });

                Assert.AreEqual(HttpStatusCode.OK, transactionResponse.StatusCode);
                Assert.AreEqual(2, transactionResponse.Count);

                TransactionalBatchOperationResult<CosmosIntegrationTestObject> createOperationResult = transactionResponse.GetOperationResultAtIndex<CosmosIntegrationTestObject>(0);
                
                Assert.IsNotNull(createOperationResult);
                Assert.IsNotNull(createOperationResult.Resource);
                Assert.AreEqual(testItem.Id, createOperationResult.Resource.Id);
                Assert.AreEqual(testItem.Pk, createOperationResult.Resource.Pk);

                TransactionalBatchOperationResult<CosmosIntegrationTestObject> readOperationResult = transactionResponse.GetOperationResultAtIndex<CosmosIntegrationTestObject>(1);
                
                Assert.IsNotNull(readOperationResult);
                Assert.IsNotNull(readOperationResult.Resource);
                Assert.AreEqual(testItem.Id, readOperationResult.Resource.Id);
                Assert.AreEqual(testItem.Pk, readOperationResult.Resource.Pk);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ConfigurationManager.BinaryEncodingEnabled, null);

                await this.container.DeleteItemAsync<CosmosIntegrationTestObject>(
                    testItem.Id,
                    new PartitionKey(testItem.Pk));
            }
        }
    }
}
