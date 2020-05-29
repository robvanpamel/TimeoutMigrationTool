﻿namespace Particular.TimeoutMigrationTool.RabbitMq
{
    using System;
    using RabbitMQ.Client;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    public class RabbitMqTimeoutCreator : ICreateTransportTimeouts
    {
        public RabbitMqTimeoutCreator(string targetConnectionString)
        {
            this.targetConnectionString = targetConnectionString;
            batchWriter = new RabbitBatchWriter(targetConnectionString);
            messagePump = new RabbitStagePump(targetConnectionString, QueueCreator.StagingQueueName);
        }

        public async Task<MigrationCheckResult> AbleToMigrate(EndpointInfo endpoint)
        {
            factory = new ConnectionFactory();
            factory.Uri = new Uri(targetConnectionString);

            await CreateStagingQueue();

            return await VerifyEndpointIsReadyForNativeTimeouts(endpoint);
        }

        public Task StageBatch(List<TimeoutData> timeouts)
        {
            return batchWriter.WriteTimeoutsToStagingQueue(timeouts, QueueCreator.StagingQueueName);
        }

        public Task CompleteBatch(int number)
        {
            return messagePump.CompleteBatch(number);
        }

        Task<MigrationCheckResult> VerifyEndpointIsReadyForNativeTimeouts(EndpointInfo endpoint)
        {
            var result = new MigrationCheckResult();
            if ((endpoint.LongestTimeout - DateTime.UtcNow).TotalSeconds > RabbitBatchWriter.MaxDelayInSeconds)
            {
                result.Problems.Add($"{endpoint.EndpointName} - has a timeout that has further away date than allowed {RabbitBatchWriter.MaxDelayInSeconds} seconds.");
            }

            using (var connection = factory.CreateConnection())
            using (var channel = connection.CreateModel())
            {
                try
                {
                    channel.QueueDeclarePassive(endpoint.EndpointName);
                }
                catch (Exception)
                {
                    result.Problems.Add($"There is no queue for an endpoint {endpoint.EndpointName}.");
                    return Task.FromResult(result);
                }

                channel.ExchangeBind(endpoint.EndpointName, "nsb.delay-delivery", $"#.{endpoint.EndpointName}");
            }

            return Task.FromResult(result);
        }

        Task CreateStagingQueue()
        {
            using (var connection = factory.CreateConnection())
            using (var channel = connection.CreateModel())
            {
                QueueCreator.CreateStagingInfrastructure(channel);
            }

            return Task.CompletedTask;
        }

        string targetConnectionString;
        ConnectionFactory factory;

        readonly RabbitStagePump messagePump;
        readonly RabbitBatchWriter batchWriter;
    }
}