﻿namespace Particular.TimeoutMigrationTool.SqlP
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    public class SqlTimeoutStorage : ITimeoutStorage
    {
        public SqlTimeoutStorage(string sourceConnectionString, SqlDialect dialect, string timeoutTableName)
        {
            connectionString = sourceConnectionString;
            this.dialect = dialect;
            this.timeoutTableName = timeoutTableName;
        }

        public Task<ToolState> GetToolState()
        {
            throw new System.NotImplementedException();
        }

        public async Task<List<BatchInfo>> Prepare(DateTime maxCutoffTime)
        {
            using (var connection = dialect.Connect(connectionString))
            {
                var command = connection.CreateCommand();
                command.CommandText = dialect.GetScriptToPrepareTimeouts(timeoutTableName, 5);

                var cutOffParameter = command.CreateParameter();
                cutOffParameter.ParameterName = "maxCutOff";
                cutOffParameter.Value = maxCutoffTime;
                command.Parameters.Add(cutOffParameter);

                var runParametersParameter = command.CreateParameter();
                runParametersParameter.ParameterName = "RunParameters";
                runParametersParameter.Value = "...where do I get this from?";
                command.Parameters.Add(runParametersParameter);

                await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                return null;
            }
        }

        public Task<List<TimeoutData>> ReadBatch(int batchNumber)
        {
            throw new System.NotImplementedException();
        }

        public Task CompleteBatch(int number)
        {
            throw new System.NotImplementedException();
        }

        public Task StoreToolState(ToolState toolState)
        {
            throw new System.NotImplementedException();
        }

        public Task Abort(ToolState toolState)
        {
            throw new NotImplementedException();
        }

        public Task<bool> CanPrepareStorage()
        {
            throw new NotImplementedException();
        }

        readonly string connectionString;
        readonly SqlDialect dialect;
        readonly string timeoutTableName;
    }
}