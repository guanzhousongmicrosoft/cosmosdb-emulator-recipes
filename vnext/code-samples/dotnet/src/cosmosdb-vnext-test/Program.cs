﻿﻿// Program.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

public class TestDocument
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("queryfield")]
    public string Queryfield { get; set; }

    [JsonProperty("pk")]
    public string PartitionKey { get; set; }  // This property must match the partition key path without leading slash

    [JsonProperty("city")]
    public string City { get; set; }
}

// Simple document matching the GitHub issue setup (partition key /id)
public class SimpleDocument
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("value")]
    public int Value { get; set; }
}

public class CosmosDbDemo
{
    // Update these constants with your own Cosmos DB endpoint and key
    private const string EndpointUrl = "https://localhost:8081";  // vnext-preview uses HTTPS by default
    private const string PrimaryKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    
    private CosmosClient cosmosClient;

    public static async Task Main(string[] args)
    {
        try
        {
            Console.WriteLine("Beginning CosmosDB Demo...");
            var demo = new CosmosDbDemo();
            
            // Print the issue summary first
            demo.PrintIssue216Summary();
            
            await demo.RunDemoAsync();
            Console.WriteLine("Demo completed successfully!");
        }
        catch (CosmosException ex)
        {
            Console.WriteLine($"Cosmos DB Error: {ex.StatusCode} - {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }

    public CosmosDbDemo()
    {
        // Configure the connection to Cosmos DB
        // For local emulator, we need to configure TLS/SSL validation
        cosmosClient = new CosmosClient(EndpointUrl, PrimaryKey, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = () => new System.Net.Http.HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            })
        });
    }

    private async Task RunDemoAsync()
    {
        // Create a unique database and container name
        string databaseName = $"db-{Guid.NewGuid():N}";
        string containerName = $"container-{Guid.NewGuid():N}";
        
        Console.WriteLine($"Creating database: {databaseName}");
        Database database = await CreateDatabaseAsync(databaseName);
        
        Console.WriteLine($"Creating container: {containerName}");
        Container container = await CreateContainerAsync(database, containerName);
        
        // Create documents with different partition keys
        string partitionKey1 = "p1";
        string partitionKey2 = "p2";
        
        Console.WriteLine("Creating documents...");
        TestDocument document1 = await CreateDocumentAsync(container, "document1", "field1", partitionKey1, "Seattle");
        TestDocument document2 = await CreateDocumentAsync(container, "document2", "field2", partitionKey2, "Portland");

        Console.WriteLine("Reading all documents unchanged...");
        await QueryAllDocumentsAsync(container);

        Console.WriteLine("\nUpdating document...");
        await UpdateDocumentAndVerifyAsync(container, "document1", partitionKey1, null); //"Chicago"

        Console.WriteLine("\nUpsert new document...");
        await UpsertDocumentAndVerifyAsync(container, "document3", "field1", partitionKey2, "New Orleans");

        Console.WriteLine("\nUpsert existing document...");
        await UpsertDocumentAndVerifyAsync(container, "document2", "field1", partitionKey2, "Miami");
        
        Console.WriteLine("Reading documents with partition key filter...");
        await QueryDocumentsByPartitionKeyAsync(container, partitionKey1);
        
        Console.WriteLine("Reading all documents...");
        await QueryAllDocumentsAsync(container);

        Console.WriteLine("\nQuerying documents with order by...");
        await QueryDocumentsWithOrderByAsync(container);

        Console.WriteLine("\nDeleting document...");
        await DeleteDocumentAndVerifyAsync(container, "document1", partitionKey1);

        Console.WriteLine("\nRunning Change Feed Demo...");
        await RunChangeFeedDemoAsync(container);

        Console.WriteLine("\nTesting Issue #216 Reproduction...");
        await TestIssue216ReproductionAsync(container);

        Console.WriteLine("\nTesting Issue #216 with Simple Container...");
        await TestIssue216SimpleContainerAsync();

        Console.WriteLine("\n🎯 Testing EXACT GitHub Issue #216 reproduction...");
        await TestGitHubIssue216ExactReproductionAsync();

        Console.WriteLine("\n🔥 Testing lupusbytes' frustration scenario...");
        await TestLupusbytesScenarioAsync();

        Console.WriteLine("\n🔬 Testing exact UpsertItemAsync + Change Feed workflow...");
        await TestExactUpsertChangeFeedWorkflowAsync();

        Console.WriteLine("Cleaning up...");
        await database.DeleteAsync();
    }

    private async Task<Database> CreateDatabaseAsync(string databaseName)
    {
        // Create a new database
        DatabaseResponse databaseResponse = await cosmosClient.CreateDatabaseIfNotExistsAsync(databaseName);
        Console.WriteLine($"Database created with status: {databaseResponse.StatusCode}");
        return databaseResponse.Database;
    }

    private async Task<Container> CreateContainerAsync(Database database, string containerName)
    {
        // Create a new container with hierarchical partition keys
        ContainerProperties containerProperties = new ContainerProperties
        {
            Id = containerName,
            PartitionKeyPaths = new List<string> { "/pk", "/queryfield" }  // Hierarchical partition key
        };

        ContainerResponse containerResponse = await database.CreateContainerIfNotExistsAsync(containerProperties);
        Console.WriteLine($"Container created with hierarchical partition keys - Status: {containerResponse.StatusCode}");
        return containerResponse.Container;
    }

    private async Task<TestDocument> CreateDocumentAsync(Container container, string id, string queryField, string partitionKey, string city)
    {
        TestDocument document = new TestDocument
        {
            Id = id,
            Queryfield = queryField,
            PartitionKey = partitionKey,
            City = city
        };

        // Create hierarchical partition key
        PartitionKey hierarchicalPK = new PartitionKeyBuilder()
            .Add(partitionKey)
            .Add(queryField)
            .Build();

        ItemResponse<TestDocument> response = await container.CreateItemAsync(document, hierarchicalPK);
        Console.WriteLine($"Created document {id} with hierarchical partition key [{partitionKey}, {queryField}] - Status: {response.StatusCode}");
        return document;
    }

    private async Task UpdateDocumentAndVerifyAsync(Container container, string id, string partitionKey, string newCity)
    {
        Console.WriteLine($"Updating document {id} with new city: {newCity}");
        
        try
        {
            // We need to determine the queryfield value to build the correct hierarchical partition key
            // First, let's query for the document to get its current queryfield value
            string sqlQuery = "SELECT * FROM c WHERE c.id = @id AND c.pk = @pk";
            QueryDefinition queryDefinition = new QueryDefinition(sqlQuery)
                .WithParameter("@id", id)
                .WithParameter("@pk", partitionKey);
            
            TestDocument existingDocument = null;
            using (var iterator = container.GetItemQueryIterator<TestDocument>(queryDefinition))
            {
                var response = await iterator.ReadNextAsync();
                existingDocument = response.FirstOrDefault();
            }
            
            if (existingDocument == null)
            {
                Console.WriteLine($"Document with id {id} not found!");
                return;
            }
            
            Console.WriteLine($"Retrieved document: Id={existingDocument.Id}, City={existingDocument.City}, Queryfield={existingDocument.Queryfield}");
            
            // Create hierarchical partition key using existing values
            PartitionKey hierarchicalPK = new PartitionKeyBuilder()
                .Add(partitionKey)
                .Add(existingDocument.Queryfield)
                .Build();
            
            // Update the property
            existingDocument.City = null;
            existingDocument.Queryfield = null;
            
            // Replace the item with the updated document
            ItemResponse<TestDocument> updateResponse = await container.ReplaceItemAsync(
                item: existingDocument,
                id: id,
                partitionKey: hierarchicalPK
            );
            
            Console.WriteLine($"Updated document - Status: {updateResponse.StatusCode}");
            
            // Query to verify the update (since queryfield is now null, we need to find it differently)
            Console.WriteLine("Verifying update by querying the document:");
            string verifyQuery = "SELECT * FROM c WHERE c.id = @id AND c.pk = @pk";
            QueryDefinition verifyQueryDef = new QueryDefinition(verifyQuery)
                .WithParameter("@id", id)
                .WithParameter("@pk", partitionKey);
            
            using (var iterator = container.GetItemQueryIterator<TestDocument>(verifyQueryDef))
            {
                var response = await iterator.ReadNextAsync();
                var updatedDocument = response.FirstOrDefault();
                if (updatedDocument != null)
                {
                    Console.WriteLine($"Verified document: Id={updatedDocument.Id}, City={updatedDocument.City}, Queryfield={updatedDocument.Queryfield}");
                }
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Console.WriteLine($"Document with id {id} not found!");
        }
    }

    private async Task UpsertDocumentAndVerifyAsync(Container container, string id, string queryField, string partitionKey, string city)
    {
        Console.WriteLine($"Upserting document {id} with hierarchical partition key [{partitionKey}, {queryField}]");
        
        try
        {
            // Create a new document or update existing one
            TestDocument document = new TestDocument
            {
                Id = id,
                Queryfield = queryField,
                PartitionKey = partitionKey,
                City = city
            };
            
            // Create hierarchical partition key
            PartitionKey hierarchicalPK = new PartitionKeyBuilder()
                .Add(partitionKey)
                .Add(queryField)
                .Build();
            
            // Upsert the document (creates if doesn't exist, or replaces if it does)
            ItemResponse<TestDocument> upsertResponse = await container.UpsertItemAsync(
                item: document,
                partitionKey: hierarchicalPK               
            );
            
            Console.WriteLine($"Upserted document - Status: {upsertResponse.StatusCode}");
            
            // Verify the upsert by reading the document
            Console.WriteLine("Verifying upsert by reading the document:");
            ItemResponse<TestDocument> verifyResponse = await container.ReadItemAsync<TestDocument>(
                id: id,
                partitionKey: hierarchicalPK
            );
            
            TestDocument upsertedDocument = verifyResponse.Resource;
            Console.WriteLine($"Verified document: Id={upsertedDocument.Id}, Queryfield={upsertedDocument.Queryfield}, " +
                            $"PartitionKey={upsertedDocument.PartitionKey}, City={upsertedDocument.City}");
        }
        catch (CosmosException ex)
        {
            Console.WriteLine($"Error upserting document: {ex.StatusCode} - {ex.Message}");
        }
    }

    private async Task DeleteDocumentAndVerifyAsync(Container container, string id, string partitionKey)
    {
        Console.WriteLine($"Deleting document {id} with partition key {partitionKey}");
        
        try
        {
            // First find the document to get its queryfield value for the hierarchical partition key
            string sqlQuery = "SELECT * FROM c WHERE c.id = @id AND c.pk = @pk";
            QueryDefinition queryDefinition = new QueryDefinition(sqlQuery)
                .WithParameter("@id", id)
                .WithParameter("@pk", partitionKey);
            
            TestDocument documentToDelete = null;
            using (var iterator = container.GetItemQueryIterator<TestDocument>(queryDefinition))
            {
                var response = await iterator.ReadNextAsync();
                documentToDelete = response.FirstOrDefault();
            }
            
            if (documentToDelete == null)
            {
                Console.WriteLine($"Document with id {id} not found!");
                return;
            }
            
            // Create hierarchical partition key
            PartitionKey hierarchicalPK = new PartitionKeyBuilder()
                .Add(partitionKey)
                .Add(documentToDelete.Queryfield)
                .Build();
            
            // Delete the document
            ItemResponse<TestDocument> deleteResponse = await container.DeleteItemAsync<TestDocument>(
                id: id,
                partitionKey: hierarchicalPK
            );
            
            Console.WriteLine($"Deleted document - Status: {deleteResponse.StatusCode}");
            
            // Verify deletion by attempting to read the document (should fail)
            Console.WriteLine("Verifying deletion by attempting to read the document:");
            try
            {
                ItemResponse<TestDocument> verifyResponse = await container.ReadItemAsync<TestDocument>(
                    id: id,
                    partitionKey: hierarchicalPK
                );
                
                // If we get here, deletion failed
                Console.WriteLine("Warning: Document still exists after deletion attempt!");
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine("Verified: Document was successfully deleted (not found anymore)");
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Console.WriteLine($"Document with id {id} not found!");
        }
        catch (CosmosException ex)
        {
            Console.WriteLine($"Error deleting document: {ex.StatusCode} - {ex.Message}");
        }
    }

    private async Task QueryDocumentsByPartitionKeyAsync(Container container, string partitionKey)
    {
        Console.WriteLine($"Querying documents with primary partition key: {partitionKey}");
        
        // Query documents using SQL with partition key filter (works with hierarchical keys)
        string sqlQuery = "SELECT * FROM c WHERE c.pk = @pk";
        QueryDefinition queryDefinition = new QueryDefinition(sqlQuery)
            .WithParameter("@pk", partitionKey);
        
        int count = 0;
        using (var iterator = container.GetItemQueryIterator<TestDocument>(queryDefinition))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var document in await iterator.ReadNextAsync())
                {
                    Console.WriteLine($"Found document: Id={document.Id}, Queryfield={document.Queryfield}, PartitionKey={document.PartitionKey}, City={document.City}");
                    count++;
                }
            }
        }
        
        Console.WriteLine($"Found {count} document(s) with primary partition key {partitionKey}");
    }

    private async Task QueryAllDocumentsAsync(Container container)
    {
        Console.WriteLine("Querying all documents...");
        
        string sqlQuery = "SELECT * FROM c";
        var queryDefinition = new QueryDefinition(sqlQuery);
        
        int count = 0;
        using (var iterator = container.GetItemQueryIterator<TestDocument>(queryDefinition))
        {
            while (iterator.HasMoreResults)
            {
                foreach (var document in await iterator.ReadNextAsync())
                {
                    Console.WriteLine($"Found document: Id={document.Id}, Queryfield={document.Queryfield}, PartitionKey={document.PartitionKey}, City={document.City}");
                    count++;
                }
            }
        }
        
        Console.WriteLine($"Found {count} document(s) in total");
    }

    private async Task QueryDocumentsWithOrderByAsync(Container container)
    {
        Console.WriteLine("\nQuerying documents ordered by City...");
        
        string sqlQuery = "SELECT * FROM c WHERE c.pk = @pk ORDER BY c.City";
        var queryDefinition = new QueryDefinition(sqlQuery)
            .WithParameter("@pk", "p1");
        var queryOptions = new QueryRequestOptions { MaxItemCount = 10 };
        
        int count = 0;
        
        Console.WriteLine("Results in ascending order by City:");
        using (var iterator = container.GetItemQueryIterator<TestDocument>(
            queryDefinition, 
            requestOptions: queryOptions))
        {
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();             
                foreach (var document in response)
                {
                    Console.WriteLine($"Found document: Id={document.Id}, City={document.City}, PartitionKey={document.PartitionKey}");
                    count++;
                }
            }
        }
        
        Console.WriteLine($"Found {count} document(s) in total");
       
        // Also demonstrate descending order
        count = 0;
        
        Console.WriteLine("\nResults in descending order by City:");
        sqlQuery = "SELECT * FROM c WHERE c.pk = @pk ORDER BY c.City DESC";
        queryDefinition = new QueryDefinition(sqlQuery)
            .WithParameter("@pk", "p1");
        
        using (var iterator = container.GetItemQueryIterator<TestDocument>(
            queryDefinition, 
            requestOptions: queryOptions))
        {
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();               
                foreach (var document in response)
                {
                    Console.WriteLine($"Found document: Id={document.Id}, City={document.City}, PartitionKey={document.PartitionKey}");
                    count++;
                }
            }
        }
        
        Console.WriteLine($"Found {count} document(s) in total");
    }

    private async Task RunChangeFeedDemoAsync(Container container)
    {
        Console.WriteLine("=== Change Feed Demo ===");
        
        // Create some test documents for change feed
        Console.WriteLine("Creating additional test documents for change feed...");
        await CreateMultipleTestDocumentsAsync(container, 5);
        
        Console.WriteLine("\nTesting Change Feed - Latest Versions Mode from Beginning...");
        await TestChangeFeedLatestVersionsModeAsync(container);
        
        Console.WriteLine("\nModifying documents and testing incremental change feed...");
        await ModifyDocumentsAndTestIncrementalChangeFeedAsync(container);
    }

    private async Task CreateMultipleTestDocumentsAsync(Container container, int documentCount)
    {
        Console.WriteLine($"Creating {documentCount} test documents...");
        
        for (int i = 0; i < documentCount; i++)
        {
            string id = $"changefeed-doc-{i}";
            string partitionKey = $"pk-{i % 2}"; // Distribute across 2 partition keys
            string queryField = $"field-{i}";
            string city = $"City-{i}";
            
            await CreateDocumentAsync(container, id, queryField, partitionKey, city);
        }
        
        Console.WriteLine($"Created {documentCount} test documents successfully");
    }

    private async Task TestChangeFeedLatestVersionsModeAsync(Container container)
    {
        Console.WriteLine("Testing Change Feed Latest Versions Mode from Beginning...");
        
        string continuationToken = null;
        int pageHintSize = 2;
        
        using FeedIterator<TestDocument> feedIterator = container.GetChangeFeedIterator<TestDocument>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion,
            new ChangeFeedRequestOptions { PageSizeHint = pageHintSize });

        int maxDocCountReturnedPerRequest = 0;
        List<TestDocument> changedDocuments = new List<TestDocument>();
        int pageCount = 0;
        
        while (feedIterator.HasMoreResults)
        {
            FeedResponse<TestDocument> response = await feedIterator.ReadNextAsync();
            pageCount++;
            
            Console.WriteLine($"Page {pageCount}: Status Code = {response.StatusCode}");
            
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                Console.WriteLine("No more changes available - received NotModified status");
                break;
            }
            else
            {
                int docCount = 0;
                foreach (var item in response)
                {
                    changedDocuments.Add(item);
                    Console.WriteLine($"  Changed document: Id={item.Id}, City={item.City}, PartitionKey={item.PartitionKey}");
                    docCount++;
                }
                maxDocCountReturnedPerRequest = Math.Max(maxDocCountReturnedPerRequest, docCount);
                Console.WriteLine($"  Documents in this page: {docCount}");
            }
            continuationToken = response.ContinuationToken;
        }

        Console.WriteLine($"Change Feed Summary:");
        Console.WriteLine($"  Total documents retrieved: {changedDocuments.Count}");
        Console.WriteLine($"  Max documents per request: {maxDocCountReturnedPerRequest}");
        Console.WriteLine($"  Total pages processed: {pageCount}");
        Console.WriteLine($"  Final continuation token: {continuationToken?.Substring(0, Math.Min(50, continuationToken.Length))}...");
    }

    private async Task ModifyDocumentsAndTestIncrementalChangeFeedAsync(Container container)
    {
        Console.WriteLine("Setting up change feed iterator from Now...");
        
        // Create iterator starting from Now to catch only new changes
        using FeedIterator<TestDocument> feedIterator = container.GetChangeFeedIterator<TestDocument>(
            ChangeFeedStartFrom.Now(),
            ChangeFeedMode.LatestVersion,
            new ChangeFeedRequestOptions { PageSizeHint = 5 });

        // First, drain any existing changes (should be empty since we're starting from Now)
        Console.WriteLine("Draining existing changes (should be none)...");
        await DrainChangeFeedAsync(feedIterator, "Initial drain");

        // Now modify some documents
        Console.WriteLine("\nModifying existing documents...");
        await UpdateDocumentAndVerifyAsync(container, "changefeed-doc-0", "pk-0", "Modified-City-0");
        await UpdateDocumentAndVerifyAsync(container, "changefeed-doc-1", "pk-1", "Modified-City-1");
        
        // Create a new document
        await CreateDocumentAsync(container, "new-doc-after-changefeed", "new-field", "pk-0", "New-City");
        
        // Now check the change feed for these modifications
        Console.WriteLine("\nChecking change feed for recent modifications...");
        List<TestDocument> recentChanges = await DrainChangeFeedAsync(feedIterator, "After modifications");
        
        Console.WriteLine($"Found {recentChanges.Count} recent changes");
        foreach (var doc in recentChanges)
        {
            Console.WriteLine($"  Recent change: Id={doc.Id}, City={doc.City}, PartitionKey={doc.PartitionKey}");
        }
    }

    private async Task<List<TestDocument>> DrainChangeFeedAsync(FeedIterator<TestDocument> feedIterator, string context)
    {
        List<TestDocument> allChanges = new List<TestDocument>();
        int pageCount = 0;
        
        Console.WriteLine($"Draining change feed ({context})...");
        
        while (feedIterator.HasMoreResults)
        {
            FeedResponse<TestDocument> response = await feedIterator.ReadNextAsync();
            pageCount++;
            
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                Console.WriteLine($"  Page {pageCount}: No more changes (NotModified)");
                break;
            }
            else
            {
                int docCount = 0;
                foreach (var item in response)
                {
                    allChanges.Add(item);
                    docCount++;
                }
                Console.WriteLine($"  Page {pageCount}: Found {docCount} changes");
            }
        }
        
        Console.WriteLine($"Completed draining change feed - Total: {allChanges.Count} documents, {pageCount} pages");
        return allChanges;
    }

    private async Task TestIssue216ReproductionAsync(Container container)
    {
        Console.WriteLine("=== Testing Issue #216 Reproduction ===");
        Console.WriteLine("Using PageSizeHint = 100 (similar to user's issue)");
        
        try
        {
            // First create some test documents
            Console.WriteLine("Creating test documents...");
            await CreateDocumentAsync(container, "issue216-doc1", "field1", "pk1", "TestCity1");
            await CreateDocumentAsync(container, "issue216-doc2", "field2", "pk2", "TestCity2");
            
            // Try to reproduce the issue with PageSizeHint = 100
            using var iterator = container.GetChangeFeedIterator<TestDocument>(
                ChangeFeedStartFrom.Beginning(),
                ChangeFeedMode.LatestVersion,
                new ChangeFeedRequestOptions { PageSizeHint = 100 });

            Console.WriteLine("Attempting to read change feed with PageSizeHint = 100...");
            
            if (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                Console.WriteLine($"✅ Success! Retrieved {response.Count} documents");
                Console.WriteLine($"Status Code: {response.StatusCode}");
                Console.WriteLine($"Continuation Token: {response.ContinuationToken?.Substring(0, Math.Min(50, response.ContinuationToken.Length))}...");
                
                foreach (var doc in response)
                {
                    Console.WriteLine($"  Document: Id={doc.Id}, City={doc.City}");
                }
            }
            else
            {
                Console.WriteLine("No results available");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error occurred (reproducing issue #216): {ex.Message}");
            Console.WriteLine($"Exception Type: {ex.GetType().Name}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
            }
        }

        // Also test with a simple container (no hierarchical partition keys)
        await TestIssue216SimpleContainerAsync();
    }

    private async Task TestIssue216SimpleContainerAsync()
    {
        Console.WriteLine("=== Testing Issue #216 with Simple Container (no hierarchical partition keys) ===");
        
        try
        {
            // Create a simple container without hierarchical partition keys (like user might have)
            string databaseName = $"simple-db-{Guid.NewGuid():N}";
            string containerName = $"simple-container-{Guid.NewGuid():N}";
            
            Console.WriteLine($"Creating simple database: {databaseName}");
            Database database = await cosmosClient.CreateDatabaseIfNotExistsAsync(databaseName);
            
            Console.WriteLine($"Creating simple container: {containerName}");
            ContainerProperties containerProperties = new ContainerProperties
            {
                Id = containerName,
                PartitionKeyPath = "/id"  // Simple single partition key like user's setup
            };
            Container container = (await database.CreateContainerIfNotExistsAsync(containerProperties)).Container;
            
            // Create simple test documents
            Console.WriteLine("Creating simple test documents...");
            var doc1 = new { id = "simple1", data = "test1" };
            var doc2 = new { id = "simple2", data = "test2" };
            
            await container.CreateItemAsync(doc1, new PartitionKey("simple1"));
            await container.CreateItemAsync(doc2, new PartitionKey("simple2"));
            
            Console.WriteLine("Testing Change Feed with simple container and PageSizeHint = 100...");
            
            // Try the exact same pattern as the user
            using var iterator = container.GetChangeFeedIterator<dynamic>(
                ChangeFeedStartFrom.Beginning(),
                ChangeFeedMode.LatestVersion,
                new ChangeFeedRequestOptions { PageSizeHint = 100 });

            if (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                Console.WriteLine($"✅ Success! Retrieved {response.Count} documents");
                Console.WriteLine($"Status Code: {response.StatusCode}");
                
                foreach (var doc in response)
                {
                    Console.WriteLine($"  Document: {doc}");
                }
            }
            else
            {
                Console.WriteLine("No results available");
            }
            
            // Clean up
            await database.DeleteAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error occurred: {ex.Message}");
            Console.WriteLine($"Exception Type: {ex.GetType().Name}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
            }
        }
    }

    // EXACT REPRODUCTION of GitHub Issue #216 code
    private async Task<(IEnumerable<SimpleDocument> documents, string continuationToken)> TestGitHubIssue216ExactCodeAsync(Container container, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("\n🔬 Testing EXACT code from GitHub Issue #216...");
        
        try
        {
            using var iterator = container.GetChangeFeedIterator<SimpleDocument>(
                ChangeFeedStartFrom.Beginning(),
                ChangeFeedMode.LatestVersion,
                new ChangeFeedRequestOptions { PageSizeHint = 100 });

            if (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                Console.WriteLine($"✅ SUCCESS - GitHub Issue #216 code worked!");
                Console.WriteLine($"Retrieved {response.Count} documents");
                Console.WriteLine($"Status Code: {response.StatusCode}");
                return (response, response.ContinuationToken);
            }

            Console.WriteLine("No more results available");
            return ([], null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ REPRODUCED GitHub Issue #216 Error: {ex.Message}");
            Console.WriteLine($"Exception Type: {ex.GetType().Name}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
            }
            
            // Check if this is the specific error reported
            if (ex.Message.Contains("cosmos_api.document_change_feed") && ex.Message.Contains("Does Not Exist"))
            {
                Console.WriteLine("🎯 CONFIRMED: This is the exact error reported in GitHub Issue #216!");
            }
            
            throw; // Re-throw to maintain the exception chain
        }
    }

    // Test the lupusbytes comment scenario - struggling with this issue for days
    private async Task TestLupusbytesScenarioAsync()
    {
        Console.WriteLine("\n🔥 Testing lupusbytes' scenario: 'Been banging my head trying to get this working for days'");
        Console.WriteLine("This reproduces the exact frustration described in GitHub Issue #216 comment");
        
        try
        {
            // Create a completely separate database and container to isolate the issue
            string frustrationDbName = "frustration-test-db";
            string frustrationContainerName = "frustration-container";
            
            Console.WriteLine($"Creating isolation test database: {frustrationDbName}");
            Database frustrationDb = await cosmosClient.CreateDatabaseIfNotExistsAsync(frustrationDbName);
            
            Console.WriteLine($"Creating isolation test container: {frustrationContainerName}");
            ContainerProperties containerProps = new ContainerProperties
            {
                Id = frustrationContainerName,
                PartitionKeyPath = "/id"  // Simple partition key like many users have
            };
            
            Container frustrationContainer = await frustrationDb.CreateContainerIfNotExistsAsync(containerProps);
            
            // Add some test data using the EXACT code snippet from GitHub Issue #216
            Console.WriteLine("Adding test documents using EXACT GitHub Issue #216 code snippet...");
            
            // Test the exact UpsertItemAsync pattern from the issue
            var myEntity1 = new SimpleDocument { Id = "test1", Name = "Test Document 1", Value = 100 };
            var myEntity2 = new SimpleDocument { Id = "test2", Name = "Test Document 2", Value = 200 };
            var myEntity3 = new SimpleDocument { Id = "test3", Name = "Test Document 3", Value = 300 };
            
            ItemRequestOptions options = null; // Exactly as in the GitHub issue
            CancellationToken cancellationToken = default;
            
            Console.WriteLine("Testing exact UpsertItemAsync pattern from GitHub Issue #216...");
            
            // This is the EXACT code snippet from the GitHub issue
            await frustrationContainer.UpsertItemAsync(
                myEntity1,
                new PartitionKey(myEntity1.Id),
                options, // null
                cancellationToken: cancellationToken);
                
            await frustrationContainer.UpsertItemAsync(
                myEntity2,
                new PartitionKey(myEntity2.Id),
                options, // null
                cancellationToken: cancellationToken);
                
            await frustrationContainer.UpsertItemAsync(
                myEntity3,
                new PartitionKey(myEntity3.Id),
                options, // null
                cancellationToken: cancellationToken);
            
            Console.WriteLine("✅ UpsertItemAsync operations completed successfully");
            
            // Now test the various approaches that users might try
            Console.WriteLine("\n--- Testing Approach 1: Basic Change Feed (what most users try first) ---");
            await TestBasicChangeFeedApproach(frustrationContainer);
            
            Console.WriteLine("\n--- Testing Approach 2: Change Feed with different PageSizeHint values ---");
            await TestDifferentPageSizeHints(frustrationContainer);
            
            Console.WriteLine("\n--- Testing Approach 3: Change Feed with Gateway Mode (recommended fix) ---");
            await TestGatewayModeChangeFeed(frustrationContainer);
            
            Console.WriteLine("\n--- Testing Approach 4: Change Feed with different start positions ---");
            await TestDifferentStartPositions(frustrationContainer);
            
            // Clean up
            await frustrationDb.DeleteAsync();
            Console.WriteLine("🧹 Cleaned up frustration test database");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Lupusbytes scenario test failed: {ex.Message}");
            Console.WriteLine($"This is exactly the kind of error users are experiencing!");
            throw;
        }
    }

    private async Task TestBasicChangeFeedApproach(Container container)
    {
        Console.WriteLine("Testing basic Change Feed approach (what users typically try first)...");
        
        try
        {
            using var iterator = container.GetChangeFeedIterator<SimpleDocument>(
                ChangeFeedStartFrom.Beginning(),
                ChangeFeedMode.LatestVersion);

            if (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                Console.WriteLine($"✅ Basic approach worked! Retrieved {response.Count} documents");
                
                foreach (var doc in response)
                {
                    Console.WriteLine($"  Document: {doc.Id} - {doc.Name} (Value: {doc.Value})");
                }
            }
            else
            {
                Console.WriteLine("No results available");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Basic approach failed: {ex.Message}");
            if (ex.Message.Contains("cosmos_api.document_change_feed"))
            {
                Console.WriteLine("🎯 This is the exact error users are hitting!");
            }
        }
    }

    private async Task TestDifferentPageSizeHints(Container container)
    {
        Console.WriteLine("Testing different PageSizeHint values...");
        
        int[] pageSizes = { 1, 10, 50, 100, 500, 1000 };
        
        foreach (int pageSize in pageSizes)
        {
            Console.WriteLine($"  Testing PageSizeHint = {pageSize}...");
            
            try
            {
                using var iterator = container.GetChangeFeedIterator<SimpleDocument>(
                    ChangeFeedStartFrom.Beginning(),
                    ChangeFeedMode.LatestVersion,
                    new ChangeFeedRequestOptions { PageSizeHint = pageSize });

                if (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    Console.WriteLine($"    ✅ PageSizeHint {pageSize} worked! Retrieved {response.Count} documents");
                }
                else
                {
                    Console.WriteLine($"    ⚠️ PageSizeHint {pageSize} - No results available");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ❌ PageSizeHint {pageSize} failed: {ex.Message}");
                if (ex.Message.Contains("cosmos_api.document_change_feed"))
                {
                    Console.WriteLine($"    🎯 PageSizeHint {pageSize} triggers the GitHub issue!");
                }
            }
        }
    }

    private async Task TestGatewayModeChangeFeed(Container container)
    {
        Console.WriteLine("Testing Change Feed with Gateway Mode (recommended fix)...");
        Console.WriteLine("Note: This test uses the same client that should already be in Gateway mode");
        
        try
        {
            using var iterator = container.GetChangeFeedIterator<SimpleDocument>(
                ChangeFeedStartFrom.Beginning(),
                ChangeFeedMode.LatestVersion,
                new ChangeFeedRequestOptions { PageSizeHint = 100 });

            if (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                Console.WriteLine($"✅ Gateway Mode approach worked! Retrieved {response.Count} documents");
                Console.WriteLine($"Status Code: {response.StatusCode}");
                
                foreach (var doc in response)
                {
                    Console.WriteLine($"  Document: {doc.Id} - {doc.Name} (Value: {doc.Value})");
                }
            }
            else
            {
                Console.WriteLine("No results available");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Gateway Mode approach failed: {ex.Message}");
            if (ex.Message.Contains("cosmos_api.lsn_sequence"))
            {
                Console.WriteLine("🎯 This is the secondary error users see after switching to Gateway Mode!");
            }
        }
    }

    private async Task TestDifferentStartPositions(Container container)
    {
        Console.WriteLine("Testing different Change Feed start positions...");
        
        var startPositions = new[]
        {
            ("Beginning", ChangeFeedStartFrom.Beginning()),
            ("Now", ChangeFeedStartFrom.Now()),
            ("Time (1 hour ago)", ChangeFeedStartFrom.Time(DateTime.UtcNow.AddHours(-1)))
        };
        
        foreach (var (name, startFrom) in startPositions)
        {
            Console.WriteLine($"  Testing start position: {name}...");
            
            try
            {
                using var iterator = container.GetChangeFeedIterator<SimpleDocument>(
                    startFrom,
                    ChangeFeedMode.LatestVersion,
                    new ChangeFeedRequestOptions { PageSizeHint = 100 });

                if (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    Console.WriteLine($"    ✅ Start position '{name}' worked! Retrieved {response.Count} documents");
                }
                else
                {
                    Console.WriteLine($"    ⚠️ Start position '{name}' - No results available");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ❌ Start position '{name}' failed: {ex.Message}");
                if (ex.Message.Contains("cosmos_api"))
                {
                    Console.WriteLine($"    🎯 Start position '{name}' triggers a cosmos_api error!");
                }
            }
        }
    }

    // Test exact GitHub Issue #216 reproduction with proper setup
    private async Task TestGitHubIssue216ExactReproductionAsync()
    {
        Console.WriteLine("\n🎯 Setting up EXACT GitHub Issue #216 test case...");
        
        try
        {
            // Create a separate database and container that matches the GitHub issue setup
            string issueDatabaseName = "my-db"; // Name mentioned in the issue
            string issueContainerName = "myentity"; // Name mentioned in the issue
            
            Console.WriteLine($"Creating issue test database: {issueDatabaseName}");
            Database issueDatabase = await cosmosClient.CreateDatabaseIfNotExistsAsync(issueDatabaseName);
            
            Console.WriteLine($"Creating issue test container: {issueContainerName} with partition key /id");
            ContainerProperties issueContainerProperties = new ContainerProperties
            {
                Id = issueContainerName,
                PartitionKeyPath = "/id"  // Simple partition key like in the issue
            };
            
            Container issueContainer = await issueDatabase.CreateContainerIfNotExistsAsync(issueContainerProperties);
            
            // Add some test data using the EXACT UpsertItemAsync pattern from GitHub Issue #216
            Console.WriteLine("Adding test documents using EXACT GitHub Issue #216 UpsertItemAsync pattern...");
            
            var myEntity1 = new SimpleDocument { Id = "test1", Name = "Document 1", Value = 100 };
            var myEntity2 = new SimpleDocument { Id = "test2", Name = "Document 2", Value = 200 };
            
            ItemRequestOptions options = null; // Exactly as in the GitHub issue
            CancellationToken cancellationToken = default;
            
            // This is the EXACT code snippet from the GitHub issue
            await issueContainer.UpsertItemAsync(
                myEntity1,
                new PartitionKey(myEntity1.Id),
                options, // null
                cancellationToken: cancellationToken);
                
            await issueContainer.UpsertItemAsync(
                myEntity2,
                new PartitionKey(myEntity2.Id),
                options, // null
                cancellationToken: cancellationToken);
            
            Console.WriteLine("Documents created using exact GitHub issue pattern. Testing Change Feed with exact GitHub issue code...");
            
            // Test the exact code from GitHub issue
            var result = await TestGitHubIssue216ExactCodeAsync(issueContainer);
            
            Console.WriteLine($"✅ Test completed successfully! Retrieved {result.documents.Count()} documents");
            
            // Clean up the test database
            await issueDatabase.DeleteAsync();
            Console.WriteLine("Issue test database cleaned up");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ GitHub Issue #216 exact reproduction test failed: {ex.Message}");
            throw;
        }
    }

    // Test the exact UpsertItemAsync + Change Feed workflow that's causing issues
    private async Task TestExactUpsertChangeFeedWorkflowAsync()
    {
        Console.WriteLine("\n🔬 Testing EXACT UpsertItemAsync + Change Feed workflow from GitHub Issue #216...");
        
        try
        {
            // Create a container that matches the exact GitHub issue setup
            string workflowDbName = "workflow-test-db";
            string workflowContainerName = "myentity"; // Exact name from GitHub issue
            
            Console.WriteLine($"Creating workflow test database: {workflowDbName}");
            Database workflowDb = await cosmosClient.CreateDatabaseIfNotExistsAsync(workflowDbName);
            
            Console.WriteLine($"Creating workflow test container: {workflowContainerName}");
            ContainerProperties containerProps = new ContainerProperties
            {
                Id = workflowContainerName,
                PartitionKeyPath = "/id"  // Exact partition key from GitHub issue
            };
            
            Container myContainer = await workflowDb.CreateContainerIfNotExistsAsync(containerProps);
            
            // Step 1: Use the exact UpsertItemAsync pattern
            Console.WriteLine("Step 1: Using exact UpsertItemAsync pattern from GitHub Issue #216...");
            
            var myEntity = new SimpleDocument { Id = "workflow-test-1", Name = "Workflow Test Document", Value = 500 };
            ItemRequestOptions options = null; // Exactly as in the GitHub issue
            CancellationToken cancellationToken = default;
            
            // This is the EXACT code snippet from the GitHub issue
            await myContainer.UpsertItemAsync(
                myEntity,
                new PartitionKey(myEntity.Id),
                options, // null
                cancellationToken: cancellationToken);
            
            Console.WriteLine("✅ UpsertItemAsync completed successfully");
            
            // Step 2: Immediately try to use Change Feed (this is where the issue occurs)
            Console.WriteLine("Step 2: Immediately testing Change Feed with PageSizeHint = 100 (exact issue trigger)...");
            
            using var iterator = myContainer.GetChangeFeedIterator<SimpleDocument>(
                ChangeFeedStartFrom.Beginning(),
                ChangeFeedMode.LatestVersion,
                new ChangeFeedRequestOptions { PageSizeHint = 100 });

            if (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                Console.WriteLine($"✅ Change Feed worked! Retrieved {response.Count} documents");
                Console.WriteLine($"Status Code: {response.StatusCode}");
                
                foreach (var doc in response)
                {
                    Console.WriteLine($"  Document: {doc.Id} - {doc.Name} (Value: {doc.Value})");
                }
            }
            else
            {
                Console.WriteLine("No results available from Change Feed");
            }
            
            // Clean up
            await workflowDb.DeleteAsync();
            Console.WriteLine("Workflow test database cleaned up");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ UpsertItemAsync + Change Feed workflow failed: {ex.Message}");
            Console.WriteLine($"Exception Type: {ex.GetType().Name}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
            }
            
            if (ex.Message.Contains("cosmos_api.document_change_feed"))
            {
                Console.WriteLine("🎯 CONFIRMED: This is the exact error from GitHub Issue #216!");
                Console.WriteLine("The combination of UpsertItemAsync + Change Feed with PageSizeHint = 100 triggers this error.");
            }
            
            throw;
        }
    }

    private void PrintIssue216Summary()
    {
        Console.WriteLine("\n" + new string('=', 80));
        Console.WriteLine("🎯 GITHUB ISSUE #216 REPRODUCTION SUMMARY");
        Console.WriteLine(new string('=', 80));
        Console.WriteLine();
        Console.WriteLine("Issue: Change Feed - function cosmos_api.document_change_feed Does Not Exist");
        Console.WriteLine("URL: https://github.com/Azure/azure-cosmos-db-emulator-docker/issues/216");
        Console.WriteLine();
        Console.WriteLine("ORIGINAL PROBLEM:");
        Console.WriteLine("- Users get error 'cosmos_api.document_change_feed Does Not Exist' when using Change Feed");
        Console.WriteLine("- Specifically happens with PageSizeHint = 100");
        Console.WriteLine("- Code works fine with real Azure Cosmos DB, but fails with vnext emulator");
        Console.WriteLine();
        Console.WriteLine("PROBLEMATIC CODE PATTERN:");
        Console.WriteLine("using var iterator = container.GetChangeFeedIterator<T>(");
        Console.WriteLine("    ChangeFeedStartFrom.Beginning(),");
        Console.WriteLine("    ChangeFeedMode.LatestVersion,");
        Console.WriteLine("    new ChangeFeedRequestOptions { PageSizeHint = 100 });");
        Console.WriteLine();
        Console.WriteLine("EXACT UPSERT PATTERN FROM ISSUE:");
        Console.WriteLine("await myContainer.UpsertItemAsync(");
        Console.WriteLine("    myEntity,");
        Console.WriteLine("    new PartitionKey(myEntity.Id),");
        Console.WriteLine("    options, // null");
        Console.WriteLine("    cancellationToken: cancellationToken)");
        Console.WriteLine();
        Console.WriteLine("SECONDARY ISSUE:");
        Console.WriteLine("- When switching to Gateway Mode (recommended fix), users get:");
        Console.WriteLine("- 'relation cosmos_api.lsn_sequence does not exist' error");
        Console.WriteLine();
        Console.WriteLine("LUPUSBYTES COMMENT:");
        Console.WriteLine("- 'Been banging my head trying to get this working for days'");
        Console.WriteLine("- This represents the typical user frustration with this issue");
        Console.WriteLine();
        Console.WriteLine("ENVIRONMENT:");
        Console.WriteLine("- SDK: .NET 9.0 with Microsoft.Azure.Cosmos 3.52.0");
        Console.WriteLine("- Platform: Aspire + vnext-preview emulator");
        Console.WriteLine("- Container: myservice-cosmosdb with partition key /id");
        Console.WriteLine();
        Console.WriteLine("EXPECTED BEHAVIOR:");
        Console.WriteLine("- Change Feed should work like it does with real Azure Cosmos DB");
        Console.WriteLine("- Gateway Mode should resolve the issue");
        Console.WriteLine();
        Console.WriteLine("ACTUAL BEHAVIOR:");
        Console.WriteLine("- Change Feed fails with postgres function errors");
        Console.WriteLine("- Gateway Mode introduces different postgres errors");
        Console.WriteLine();
        Console.WriteLine("THIS TEST REPRODUCES:");
        Console.WriteLine("1. The exact code pattern from the GitHub issue");
        Console.WriteLine("2. The frustration scenario described by lupusbytes");
        Console.WriteLine("3. Different approaches users might try");
        Console.WriteLine("4. Various PageSizeHint values to identify the trigger");
        Console.WriteLine("5. Different start positions for comprehensive testing");
        Console.WriteLine();
        Console.WriteLine(new string('=', 80));
        Console.WriteLine();
    }
}