using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Microsoft.Extensions.Options;

namespace Infrastructure.Elasticsearch;

// Runs before the Kafka consumer (registered first in InfrastructureModule).
// Creates the "products" index with explicit field mappings when it does not already exist.
public sealed class ElasticsearchIndexInitializer(
    ElasticsearchClient client,
    IOptions<ElasticsearchOptions> options,
    ILogger<ElasticsearchIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var indexName = options.Value.IndexName;

        try
        {
            var existsResponse = await client.Indices.ExistsAsync(indexName, cancellationToken);
            if (existsResponse.Exists)
            {
                logger.LogInformation(
                    "Elasticsearch index '{Index}' already exists — skipping creation", indexName);
                return;
            }

            logger.LogInformation("Creating Elasticsearch index '{Index}'", indexName);

            var createRequest = new CreateIndexRequest(indexName)
            {
                Mappings = new TypeMapping
                {
                    Properties = new Properties
                    {
                        // Full-text search fields (boost applied at query time, not mapping)
                        ["name"] = new TextProperty(),
                        ["description"] = new TextProperty(),

                        // Keyword fields for exact-match filtering
                        ["categoryId"]  = new KeywordProperty(),
                        ["currency"] = new KeywordProperty(),
                        ["status"] = new KeywordProperty(),
                        ["sellerId"] = new KeywordProperty(),
                        ["productType"] = new KeywordProperty(),

                        // Numeric fields for range filters (price < 50, stock > 0)
                        ["price"] = new FloatNumberProperty(),
                        ["stock"] = new IntegerNumberProperty(),

                        // Flattened allows payload-filter queries like attributes.color: red
                        // without requiring a nested object structure
                        ["attributes"] = new FlattenedProperty(),

                        // Temporal fields
                        ["createdAt"] = new DateProperty(),
                        ["updatedAt"] = new DateProperty(),

                        // Image URLs are stored but never searched
                        ["imageUrls"] = new KeywordProperty { Index = false },

                        // CatalogItem listing summary fields
                        ["minPrice"] = new FloatNumberProperty(),
                        ["minPriceCurrency"] = new KeywordProperty(),
                        ["sellerCount"] = new IntegerNumberProperty(),
                        ["hasActiveListings"] = new BooleanProperty(),
                        ["bestCondition"] = new KeywordProperty(),
                        ["totalStock"] = new IntegerNumberProperty(),
                    }
                }
            };

            var createResponse = await client.Indices.CreateAsync(createRequest, cancellationToken);

            if (!createResponse.IsValidResponse)
                logger.LogError(
                    "Failed to create Elasticsearch index '{Index}': {Error}",
                    indexName, createResponse.ElasticsearchServerError?.ToString());
            else
                logger.LogInformation(
                    "Successfully created Elasticsearch index '{Index}'", indexName);
        }
        catch (Exception ex)
        {
            // Don't rethrow - let she do..... i mean the app start. the health endpoint will signal not-ready
            // and k8s will restart the pod when Elasticsearch comes up
            logger.LogError(ex,
                "Error during Elasticsearch index initialisation for '{Index}'", indexName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
