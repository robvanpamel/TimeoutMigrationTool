﻿namespace Particular.TimeoutMigrationTool.SqlT
{
    using System.Data;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;

    public static class SqlTQueueCreator
    {
        public static async Task CreateStagingQueue(SqlConnection connection, string tableName, string databaseName)
        {
            await using var transaction = connection.BeginTransaction();
            var sql = string.Format(SqlConstants.CreateDelayedMessageStoreText, tableName, databaseName);
            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandType = CommandType.Text
            };
            await command.ExecuteScalarAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        public static async Task<bool> DoesDelayedDeliveryTableExist(SqlConnection connection, string tableName, string schema, string databaseName)
        {
            var sql = string.Format(SqlConstants.DelayedMessageStoreExistsText, tableName, schema, databaseName);
            await using var command = new SqlCommand(sql, connection)
            {
                CommandType = CommandType.Text
            };
            var result = await command.ExecuteScalarAsync().ConfigureAwait(false) as int?;
            return result == 1;
        }
    }
}