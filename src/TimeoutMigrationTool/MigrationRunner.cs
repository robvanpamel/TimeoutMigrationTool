namespace Particular.TimeoutMigrationTool
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using Microsoft.Extensions.Logging;

    public class MigrationRunner
    {
        public MigrationRunner(ILogger logger, ITimeoutStorage timeoutStorage, ICreateTransportTimeouts transportTimeoutsCreator)
        {
            this.logger = logger;
            this.timeoutStorage = timeoutStorage;
            this.transportTimeoutsCreator = transportTimeoutsCreator;
        }

        public async Task Run(DateTime cutOffTime, EndpointFilter endpointFilter, IDictionary<string, string> runParameters)
        {
            var watch = new Stopwatch();
            watch.Start();
            
            var toolState = await timeoutStorage.TryLoadOngoingMigration();

            if (toolState != null)
            {
                GuardAgainstInvalidState(runParameters, toolState);

                logger.LogInformation($"Existing migration for {toolState.EndpointName} found. Resuming...");

                await Run(toolState);

                if (!endpointFilter.IncludeAllEndpoints)
                {
                    return;
                }
            }

            logger.LogInformation("Listing the endpoints");
            var allEndpoints = await timeoutStorage.ListEndpoints(cutOffTime);

            var endpointsToMigrate = allEndpoints.Where(e => e.NrOfTimeouts > 0 && endpointFilter.ShouldInclude(e.EndpointName))
                .ToList();

            if (!endpointsToMigrate.Any())
            {
                if (allEndpoints.Any())
                {
                    var endpointNames = string.Join(",", allEndpoints.Select(e => e.EndpointName));

                    logger.LogInformation($"None of the endpoints ({endpointNames}) found matched the filter criteria.");
                }
                else
                {
                    logger.LogInformation("No endpoints found in storage with timeouts that needs migration");
                }
                return;
            }

            var problematicEndpoints = new List<(EndpointInfo Endpoint, List<string> Problems)>();
            foreach (var endpoint in endpointsToMigrate)
            {
                logger.LogInformation($"Verifying that timeouts set by {endpoint.EndpointName} can be migrated");
                var migrationCheckResult = await transportTimeoutsCreator.AbleToMigrate(endpoint);

                if (!migrationCheckResult.CanMigrate)
                {
                    problematicEndpoints.Add((endpoint, migrationCheckResult.Problems));
                }
                else
                {
                    logger.LogInformation($"For endpoint {endpoint.EndpointName}, {endpoint.NrOfTimeouts} are elegible to migrate.");
                }
            }

            if (problematicEndpoints.Any())
            {
                var sb = new StringBuilder();

                sb.AppendLine("Migration aborted:");
                foreach (var problematicEndpoint in problematicEndpoints)
                {
                    sb.AppendLine($"{problematicEndpoint.Endpoint.EndpointName}:");

                    foreach (var problem in problematicEndpoint.Problems)
                    {
                        sb.AppendLine($"\t - {problem}");
                    }
                }

                throw new Exception(sb.ToString());
            }

            foreach (var endpointToMigrate in endpointsToMigrate)
            {
                if (endpointToMigrate.NrOfTimeouts == 0)
                {
                    logger.LogInformation($"No timeouts found for {endpointToMigrate.EndpointName} migration will be skipped");
                    continue;
                }

                logger.LogInformation($"Starting migration for {endpointToMigrate.EndpointName}({endpointToMigrate.NrOfTimeouts} timeout(s) between {endpointToMigrate.ShortestTimeout} - {endpointToMigrate.LongestTimeout})");

                await Run(cutOffTime, endpointToMigrate.EndpointName, runParameters);
            }

            watch.Stop();
            logger.LogInformation($"Migration completed successfully in {watch.Elapsed.ToString("hh\\:mm\\:ss")}.");
        }

        async Task Run(DateTime cutOffTime, string endpointName, IDictionary<string, string> runParameters)
        {
            var toolState = await timeoutStorage.Prepare(cutOffTime, endpointName, runParameters);
            logger.LogInformation("Storage has been prepared for migration.");
            await Run(toolState);
        }

        async Task Run(IToolState toolState)
        {
            BatchInfo batch;
            
            while ((batch = await toolState.TryGetNextBatch()) != null)
            {
                logger.LogInformation($"Migrating batch {batch.Number}");

                if (batch.State == BatchState.Pending)
                {
                    logger.LogDebug($"Reading batch number {batch.Number}");
                    var timeouts = await timeoutStorage.ReadBatch(batch.Number);
                    if (timeouts.Count != batch.NumberOfTimeouts)
                    {
                        throw new Exception($"Expected to retrieve {batch.NumberOfTimeouts} timeouts but only found {timeouts.Count}");
                    }

                    logger.LogDebug($"Staging batch number {batch.Number}");
                    var stagedTimeoutCount = await transportTimeoutsCreator.StageBatch(timeouts);
                    if (batch.NumberOfTimeouts != stagedTimeoutCount)
                    {
                        throw new InvalidOperationException($"The amount of staged timeouts does not match the amount of timeouts in the batch of a number: {batch.Number}. Staged amount of timeouts: {stagedTimeoutCount}, batch contains {batch.NumberOfTimeouts}.");
                    }

                    batch.State = BatchState.Staged;
                    await timeoutStorage.MarkBatchAsStaged(batch.Number);
                }

                logger.LogDebug($"Migrating batch number {batch.Number} from staging to destination");
                var completedTimeoutsCount = await transportTimeoutsCreator.CompleteBatch(batch.Number);

                if (batch.NumberOfTimeouts != completedTimeoutsCount)
                {
                    throw new InvalidOperationException($"The amount of completed timeouts does not match the amount of timeouts in the batch of a number: {batch.Number}. Completed amount of timeouts: {completedTimeoutsCount}, batch contains {batch.NumberOfTimeouts}.");
                }

                batch.State = BatchState.Completed;
                await timeoutStorage.MarkBatchAsCompleted(batch.Number);

                logger.LogDebug($"Batch number {batch.Number} fully migrated");
            }

            await timeoutStorage.Complete();
        }

        void GuardAgainstInvalidState(IDictionary<string, string> runParameters, IToolState toolState)
        {
            if (RunParametersAreDifferent(runParameters, toolState))
            {
                var sb = new StringBuilder();

                sb.AppendLine("In progress migration parameters didn't match, either rerun with the --abort option or adjust the parameters to match to continue the current migration:");
                sb.AppendLine($"\t'--endpoint': '{toolState.EndpointName}'.");

                foreach (var setting in toolState.RunParameters)
                {
                    sb.AppendLine($"\t'{setting.Key}': '{setting.Value}'.");
                }

                throw new Exception(sb.ToString());
            }

            logger.LogInformation("Resuming in progress migration");
        }


        bool RunParametersAreDifferent(IDictionary<string, string> runParameters, IToolState currentRunState)
        {
            var currentRunParameters = currentRunState.RunParameters;

            if (runParameters.Count != currentRunParameters.Count)
            {
                return true;
            }

            foreach (var parameterKey in runParameters.Keys)
            {
                if (!currentRunParameters.ContainsKey(parameterKey))
                {
                    return true;
                }

                if (!string.Equals(runParameters[parameterKey], currentRunParameters[parameterKey], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        readonly ILogger logger;
        readonly ITimeoutStorage timeoutStorage;
        readonly ICreateTransportTimeouts transportTimeoutsCreator;
    }
}