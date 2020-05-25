using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Particular.TimeoutMigrationTool;
using Particular.TimeoutMigrationTool.RavenDB;

namespace TimeoutMigrationTool.Raven4.Tests
{
    public class RavenPreparesTheMigration : RavenTimeoutStorageTestSuite
    {
        private readonly int nrOfTimeouts = 1500;

        [SetUp]
        public async Task Setup()
        {
            await InitTimeouts(nrOfTimeouts);
        }

        [Test]
        public async Task WhenGettingTimeoutStateAndNoneIsFoundNullIsReturned()
        {
            var timeoutStorage = new RavenDBTimeoutStorage(ServerName, databaseName, "TimeoutDatas", RavenDbVersion.Four);
            var toolState = await timeoutStorage.GetToolState();

            Assert.That(toolState, Is.Null);
        }

        [Test]
        public async Task WhenGettingTimeoutStateAndOneIsFoundWeReturnIt()
        {
            await SaveToolState(SetupToolState(DateTime.Now.AddDays(-1)));

            var timeoutStorage = new RavenDBTimeoutStorage(ServerName, databaseName, "TimeoutDatas", RavenDbVersion.Four);
            var retrievedToolState = await timeoutStorage.GetToolState();

            Assert.That(retrievedToolState, Is.Not.Null);
            Assert.That(retrievedToolState.Status, Is.EqualTo(MigrationStatus.NeverRun));
            Assert.IsEmpty(retrievedToolState.Batches);
        }

        [Test]
        public async Task WhenTheStorageHasNotBeenPreparedWeWantToInitBatches()
        {
            var timeoutStorage =
                new RavenDBTimeoutStorage(ServerName, databaseName, "TimeoutDatas", RavenDbVersion.Four);
            var batches = await timeoutStorage.Prepare(DateTime.Now.AddDays(-1));

            Assert.That(batches.Count, Is.EqualTo(2));
            Assert.That(batches.First().TimeoutIds.Length, Is.EqualTo(RavenConstants.DefaultPagingSize));
            Assert.That(batches.Skip(1).First().TimeoutIds.Length,
                Is.EqualTo(nrOfTimeouts - RavenConstants.DefaultPagingSize));
        }

        [Test]
        public async Task WhenTheStorageHasBeenPreparedWeReturnStoredBatches()
        {
            await SaveToolState(SetupToolState(DateTime.Now.AddDays(-1), MigrationStatus.StoragePrepared));
            await SetupExistingBatchInfoInDatabase();

            var sut = new RavenDBTimeoutStorage(ServerName, databaseName, "TimeoutDatas", RavenDbVersion.Four);
            var batches = await sut.Prepare(DateTime.Now.AddDays(-1));

            Assert.That(batches.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task WhenCleaningUpBatchesThenTimeoutsInIncompleteBatchesAreReset()
        {
            SetupToolState(DateTime.Now.AddDays(-1));
            var preparedBatches = await SetupExistingBatchInfoInDatabase();
            var incompleteBatches = preparedBatches.Skip(1).Take(1);
            var incompleteBatch = incompleteBatches.First();

            var sut = new RavenDBTimeoutStorage(ServerName, databaseName, "TimeoutDatas", RavenDbVersion.Four);
            await sut.CleanupExistingBatchesAndResetTimeouts(incompleteBatches);

            var ravenDbReader = new RavenDbReader(ServerName, databaseName, RavenDbVersion.Four);
            var incompleteBatchFromStorage = await ravenDbReader.GetItem<BatchInfo>($"batch/{incompleteBatch.Number}");
            var resetTimeouts = await ravenDbReader.GetItems<TimeoutData>(x => incompleteBatch.TimeoutIds.Contains(x.Id), "TimeoutDatas", CancellationToken.None).ConfigureAwait(false);

            Assert.That(incompleteBatchFromStorage, Is.Null);
            Assert.That(resetTimeouts.Select(t => t.OwningTimeoutManager), Is.All.Matches<string>(x => !x.StartsWith(RavenConstants.MigrationPrefix)));
        }

        [Test]
        public async Task WhenCleaningUpBatchesThenTimeoutsInCompleteBatchesAreNotReset()
        {
            SetupToolState(DateTime.Now.AddDays(-1));
            var preparedBatches = await SetupExistingBatchInfoInDatabase();
            var incompleteBatches = preparedBatches.Skip(1).Take(1);
            var completeBatches = preparedBatches.Take(1);
            var completeBatch = completeBatches.First();

            var sut = new RavenDBTimeoutStorage(ServerName, databaseName, "TimeoutDatas", RavenDbVersion.Four);
            await sut.CleanupExistingBatchesAndResetTimeouts(incompleteBatches);

            var ravenDbReader = new RavenDbReader(ServerName, databaseName, RavenDbVersion.Four);
            var completeBatchFromStorage = await ravenDbReader.GetItem<BatchInfo>($"batch/{completeBatch.Number}");
            var resetTimeouts = await ravenDbReader.GetItems<TimeoutData>(x => completeBatch.TimeoutIds.Contains(x.Id), "TimeoutDatas", CancellationToken.None).ConfigureAwait(false);

            Assert.That(completeBatchFromStorage, Is.Null);
            Assert.That(resetTimeouts.Select(t => t.OwningTimeoutManager), Is.All.Matches<string>(x => x.StartsWith(RavenConstants.MigrationPrefix)));
        }

    }
}