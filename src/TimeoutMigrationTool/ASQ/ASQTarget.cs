﻿namespace Particular.TimeoutMigrationTool.ASQ
{
    using Microsoft.Azure.Cosmos.Table;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public class ASQTarget : ITimeoutsTarget
    {
        public ASQTarget(ILogger logger, string connectionString, IProvideDelayedDeliveryTableName delayedDeliveryTableNameProvider)
        {
            this.connectionString = connectionString;

            var cloudStorageAccount = CloudStorageAccount.Parse(connectionString);
            client = cloudStorageAccount.CreateCloudTableClient();
            this.delayedDeliveryTableNameProvider = delayedDeliveryTableNameProvider;
        }

        public async ValueTask<MigrationCheckResult> AbleToMigrate(EndpointInfo endpoint)
        {
            var migrationsResult = new MigrationCheckResult();

            try
            {
                var table = client.GetTableReference(delayedDeliveryTableNameProvider.GetDelayedDeliveryTableName(endpoint.EndpointName));
                var exists = await table.ExistsAsync();
                if (!exists)
                {
                    migrationsResult.Problems.Add($"Target delayed delivery table {delayedDeliveryTableNameProvider.GetDelayedDeliveryTableName(endpoint.EndpointName)} does not exist.");
                }
            }
            catch (StorageException ex)
            {
                migrationsResult.Problems.Add($"Unable to connect to the storage instance with connection string '{connectionString}'. Exception message '{ex.Message}'");
            }

            try
            {
                await EnsureStagingTableExists($"{delayedDeliveryTableNameProvider.GetStagingTableName(endpoint.EndpointName)}");
            }
            catch (StorageException ex)
            {
                migrationsResult.Problems.Add($"Unable to create staging queue '{delayedDeliveryTableNameProvider.GetDelayedDeliveryTableName(endpoint.EndpointName)}staging'. Exception message '{ex.Message}'");
            }

            return migrationsResult;
        }

        public ValueTask Abort(string endpointName)
        {
            return DeleteTable(delayedDeliveryTableNameProvider.GetStagingTableName(endpointName));
        }

        public async ValueTask Complete(string endpointName)
        {
            await EnsureStagingTableIsEmpty(delayedDeliveryTableNameProvider.GetStagingTableName(endpointName));
            await DeleteTable(delayedDeliveryTableNameProvider.GetStagingTableName(endpointName));
        }

        public async ValueTask<ITimeoutsTarget.IEndpointTargetBatchMigrator> PrepareTargetEndpointBatchMigrator(string endpointName)
        {
            await EnsureStagingTableExists(delayedDeliveryTableNameProvider.GetStagingTableName(endpointName));

            return new ASQEndpointMigrator(client, delayedDeliveryTableNameProvider.GetDelayedDeliveryTableName(endpointName), delayedDeliveryTableNameProvider.GetStagingTableName(endpointName));
        }

        async Task EnsureStagingTableIsEmpty(string stagingTableName)
        {
            var table = client.GetTableReference(stagingTableName);

            var results = await table.ExecuteQueryAsync(new TableQuery<DelayedMessageEntity>(), CancellationToken.None);
            if (results.Any())
            {
                throw new Exception($"Unable to complete migration as there are still records available in the staging table.");
            }
        }

        async Task EnsureStagingTableExists(string stagingQueueName)
        {
            var table = client.GetTableReference(stagingQueueName);
            await table.CreateIfNotExistsAsync();
        }

        async ValueTask DeleteTable(string tableName)
        {
            var table = client.GetTableReference(tableName);
            await table.DeleteIfExistsAsync();
        }

        string connectionString;
        CloudTableClient client;
        IProvideDelayedDeliveryTableName delayedDeliveryTableNameProvider;
    }
}