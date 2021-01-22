namespace TimeoutMigrationTool.SqlT.IntegrationTests
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using NUnit.Framework;
    using Particular.TimeoutMigrationTool;
    using Particular.TimeoutMigrationTool.SqlT;
    using SqlP.AcceptanceTests;

    [TestFixture]
    public class SqlTTimeoutsTargetTests
    {
        private string databaseName;
        private string connectionString;

        string ExistingEndpointName = "ExistingEndpointName";
        string ExistingDestination = "ExistingEndpointNameDirect";

        [SetUp]
        public async Task SetUp()
        {
            databaseName = $"IntegrationTests{TestContext.CurrentContext.Test.ID.Replace("-", "")}";

            connectionString = $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={databaseName};Integrated Security=True;";

            await MsSqlMicrosoftDataClientHelper.RecreateDbIfNotExists(connectionString);
        }

        [TearDown]
        public async Task TearDown()
        {
            await MsSqlMicrosoftDataClientHelper.RemoveDbIfExists(connectionString);
        }

        [Test]
        public async Task AbleToMigrate_delayed_delivery_does_exist_should_indicate_no_problems()
        {
            var sut = new SqlTTimeoutsTarget(new TestLoggingAdapter(), connectionString, "dbo");

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = string.Format(@"
CREATE TABLE [{1}].[{0}] (
    RowVersion bigint IDENTITY(1,1) NOT NULL
);
", $"{ExistingEndpointName}.Delayed", "dbo");
            await command.ExecuteNonQueryAsync();

            var info = new EndpointInfo
            {
                EndpointName = ExistingEndpointName,
                ShortestTimeout = DateTime.UtcNow.AddDays(3),
                LongestTimeout = DateTime.UtcNow.AddDays(5),
                Destinations = new List<string>
                {
                    ExistingDestination
                }
            };
            var result = await sut.AbleToMigrate(info);

            Assert.IsTrue(result.CanMigrate);
        }

        [Test]
        public async Task AbleToMigrate_delayed_delivery_does_not_exist_should_indicate_problems()
        {
            var sut = new SqlTTimeoutsTarget(new TestLoggingAdapter(), connectionString, "dbo");

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = string.Format(@"
IF OBJECT_ID('{0}.{1}', 'u') IS NOT NULL
  DROP TABLE {0}.{1};
", $"{ExistingEndpointName}.Delayed", "dbo");
            await command.ExecuteNonQueryAsync();

            var info = new EndpointInfo
            {
                EndpointName = ExistingEndpointName,
                ShortestTimeout = DateTime.UtcNow.AddDays(3),
                LongestTimeout = DateTime.UtcNow.AddDays(5),
                Destinations = new List<string>
                {
                    ExistingDestination
                }
            };
            var result = await sut.AbleToMigrate(info);

            Assert.IsFalse(result.CanMigrate);
        }

        [Test]
        public async Task Should_delete_staging_queue_when_aborting()
        {
            var sut = new SqlTTimeoutsTarget(new TestLoggingAdapter(), connectionString, "dbo");
            var endpointName = "FakeEndpoint";
            await using var endpointTarget = await sut.Migrate(endpointName);
            await sut.Abort(endpointName);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = string.Format(@"
   SELECT COUNT(*)
   FROM INFORMATION_SCHEMA.TABLES
   WHERE TABLE_SCHEMA = '{1}' AND TABLE_NAME = '{0}' AND TABLE_CATALOG = '{2}'
", "timeoutmigrationtoolstagingtable", "dbo", databaseName);
            var result = await command.ExecuteScalarAsync() as int?;

            Assert.That(Convert.ToBoolean(result), Is.False);
        }
    }
}