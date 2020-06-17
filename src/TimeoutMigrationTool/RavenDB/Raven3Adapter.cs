using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Particular.TimeoutMigrationTool.RavenDB.HttpCommands;

namespace Particular.TimeoutMigrationTool.RavenDB
{
    public class Raven3Adapter : ICanTalkToRavenVersion
    {
        readonly string serverUrl;
        readonly string databaseName;
        static readonly HttpClient httpClient = new HttpClient();

        public Raven3Adapter(string serverUrl, string databaseName)
        {
            this.serverUrl = serverUrl;
            this.databaseName = databaseName;
        }

        public async Task UpdateDocument(string key, object document)
        {
            var bulkUpdateUrl = $"{serverUrl}/databases/{databaseName}/bulk_docs";
            var command = new[] {
                new
                {
                    Key = key,
                    Method = "PUT",
                    Document = document,
                    Metadata = new object()
                }
            };

            var serializedCommands = JsonConvert.SerializeObject(command);
            using var result = await httpClient.PostAsync(bulkUpdateUrl, new StringContent(serializedCommands, Encoding.UTF8, "application/json"));
            result.EnsureSuccessStatusCode();
        }

        public async Task DeleteDocument(string key)
        {
            var bulkUpdateUrl = $"{serverUrl}/databases/{databaseName}/bulk_docs";
            var deleteCommand = GetDeleteCommand(key);
            var command = new[]
            {
                deleteCommand
            };
            var serializedCommands = JsonConvert.SerializeObject(command);
            using var result = await httpClient.PostAsync(bulkUpdateUrl, new StringContent(serializedCommands, Encoding.UTF8, "application/json"));
            result.EnsureSuccessStatusCode();
        }

        public async Task CreateBatchAndUpdateTimeouts(RavenBatch batch)
        {
            var commands = batch.TimeoutIds.Select(timeoutId =>
            {
                return new Raven3BatchCommand
                {
                    Key = timeoutId,
                    Method = "EVAL",
                    DebugMode = false,
                    Patch = new Patch()
                    {
                        Script = $"this.OwningTimeoutManager = '{RavenConstants.MigrationOngoingPrefix}' + this.OwningTimeoutManager;",
                        Values = new { }
                    }
                };
            }).Cast<object>().ToList();

            commands.Add(new
            {
                Method = "PUT",
                Key = $"{RavenConstants.BatchPrefix}/{batch.Number}",
                Document = batch,
                Metadata = new object()
            });

            await PostToBulkDocs(commands);
        }

        public async Task DeleteBatchAndUpdateTimeouts(RavenBatch batch)
        {
            var deleteCommand = GetDeleteCommand($"{RavenConstants.BatchPrefix}/{batch.Number}");
            var commands = batch.TimeoutIds.Select(timeoutId =>
            {
                return new Raven3BatchCommand
                {
                    Key = timeoutId,
                    Method = "EVAL",
                    DebugMode = false,
                    Patch = new Patch()
                    {
                        Script = $"this.OwningTimeoutManager = this.OwningTimeoutManager.substr({RavenConstants.MigrationOngoingPrefix.Length});",
                        Values = new { }
                    }
                };
            }).ToList();
            commands.Add(deleteCommand);

            await PostToBulkDocs(commands);
        }

        public async Task CompleteBatchAndUpdateTimeouts(RavenBatch batch)
        {
            var updateCommand = new
            {
                Key = $"{RavenConstants.BatchPrefix}/{batch.Number}",
                Method = "PUT",
                Document = batch,
                Metadata = new object()
            };
            var commands = batch.TimeoutIds.Select(timeoutId =>
            {
                return new Raven3BatchCommand
                {
                    Key = timeoutId,
                    Method = "EVAL",
                    DebugMode = false,
                    Patch = new Patch()
                    {
                        Script = $"this.OwningTimeoutManager = '{RavenConstants.MigrationDonePrefix}' + this.OwningTimeoutManager.substr({RavenConstants.MigrationOngoingPrefix.Length});",
                        Values = new { }
                    }
                };
            }).Cast<object>().ToList();
            commands.Add(updateCommand);

            await PostToBulkDocs(commands);
        }

        public async Task ArchiveDocument(string archivedToolStateId, RavenToolState toolState)
        {
            var ravenToolStateDto = RavenToolStateDto.FromToolState(toolState);
            var insertCommand = new
            {
                Method = "PUT",
                Key = archivedToolStateId,
                Document = ravenToolStateDto,
                Metadata = new object()
            };
            var deleteCommand = GetDeleteCommand(RavenConstants.ToolStateId);

            var commands = ravenToolStateDto.Batches.Select(b => GetDeleteCommand(b)).Cast<object>().ToList();
            commands.Add(insertCommand);
            commands.Add(deleteCommand);

            await PostToBulkDocs(commands);
        }

        public async Task BatchDelete(string[] keys)
        {
            var commands = keys.Select(GetDeleteCommand);
            await PostToBulkDocs(commands);
        }

        public async Task<List<T>> GetDocuments<T>(Func<T, bool> filterPredicate, string documentPrefix, Action<T, string> idSetter, int pageSize = RavenConstants.DefaultPagingSize) where T : class
        {
            var items = new List<T>();
            var url = $"{serverUrl}/databases/{databaseName}/docs?startsWith={Uri.EscapeDataString(documentPrefix)}&pageSize={pageSize}";
            var checkForMoreResults = true;
            var iteration = 0;

            while (checkForMoreResults)
            {
                var skipFirst = $"&start={iteration * pageSize}";
                var getUrl = iteration == 0 ? url : url + skipFirst;
                using var result = await httpClient.GetAsync(getUrl);

                if (result.StatusCode == HttpStatusCode.OK)
                {
                    var pagedTimeouts = await GetDocumentsFromResponse(result.Content, idSetter);
                    if (pagedTimeouts.Count == 0 || pagedTimeouts.Count < pageSize)
                        checkForMoreResults = false;

                    var elegibleItems = pagedTimeouts.Where(filterPredicate);
                    items.AddRange(elegibleItems);
                    iteration++;
                }
            }

            return items;
        }

        public async Task<List<T>> GetPagedDocuments<T>(string documentPrefix, Action<T, string> idSetter, int startFrom, int nrOfPages) where T : class
        {
            var items = new List<T>();
            var url = $"{serverUrl}/databases/{databaseName}/docs?startsWith={Uri.EscapeDataString(documentPrefix)}&pageSize={RavenConstants.DefaultPagingSize}";

            var checkForMoreResults = true;
            var fetchStartFrom = startFrom;
            var iteration = 0;

            while (checkForMoreResults)
            {
                var skipFirst = $"&start={fetchStartFrom}";
                var getUrl = fetchStartFrom == 0 ? url : url + skipFirst;
                using var result = await httpClient.GetAsync(getUrl);

                if (result.StatusCode == HttpStatusCode.OK)
                {
                    var pagedTimeouts = await GetDocumentsFromResponse(result.Content, idSetter);

                    items.AddRange(pagedTimeouts);
                    fetchStartFrom += pagedTimeouts.Count;
                    iteration++;

                    if (iteration == nrOfPages)
                    {
                        checkForMoreResults = false;
                    }
                    else if (pagedTimeouts.Count == 0 || pagedTimeouts.Count < RavenConstants.DefaultPagingSize)
                    {
                        checkForMoreResults = false;
                    }
                }
            }

            return items;
        }

        public async Task<T> GetDocument<T>(string id, Action<T, string> idSetter) where T : class
        {
            var url = $"{serverUrl}/databases/{databaseName}/docs?id={Uri.EscapeDataString(id)}";
            using var response = await httpClient.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return default(T);
            }

            var document = await GetDocumentFromResponse<T>(response.Content);
            idSetter(document, id);
            return document;
        }

        public async Task<List<T>> GetDocuments<T>(IEnumerable<string> ids) where T : class
        {
            if (!ids.Any())
            {
                return new List<T>();
            }

            var url = $"{serverUrl}/databases/{databaseName}/docs?";
            var queryStringIds = ids.Select(id => $"id={Uri.EscapeDataString(id)}").ToList();
            var uris = new List<string>();
            var uriBuilder = new StringBuilder(RavenConstants.MaxUriLength, RavenConstants.MaxUriLength);
            uriBuilder.Append(url);

            while (queryStringIds.Any())
            {
                try
                {
                    var idQry = queryStringIds.First();
                    uriBuilder.Append($"{idQry}&");
                    queryStringIds.Remove(idQry);
                }
                catch (ArgumentOutOfRangeException)
                {
                    var uri = uriBuilder.ToString().TrimEnd('&');
                    uris.Add(uri);
                    uriBuilder = new StringBuilder(RavenConstants.MaxUriLength, RavenConstants.MaxUriLength);
                    uriBuilder.Append(url);
                }
            }
            uris.Add(uriBuilder.ToString().TrimEnd('&'));

            var results = new List<T>();
            foreach (var uri in uris)
            {
                using var response = await httpClient.GetAsync(uri);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    continue;

                var resultsFromUri = await GetDocumentsFromResponse<T>(response.Content);
                results.AddRange(resultsFromUri);
            }

            return results;
        }

        private async Task<List<T>> GetDocumentsFromResponse<T>(HttpContent resultContent, Action<T, string> idSetter) where T : class
        {
            var results = new List<T>();

            var contentString = await resultContent.ReadAsStringAsync();
            var jArray = JArray.Parse(contentString);

            foreach (var item in jArray)
            {
                var document = JsonConvert.DeserializeObject<T>(item.ToString());
                var id = (string)((dynamic)item)["@metadata"]["@id"];
                idSetter(document, id);
                results.Add(document);
            }

            return results;
        }

        private async Task<List<T>> GetDocumentsFromResponse<T>(HttpContent resultContent) where T : class
        {
            var results = new List<T>();

            var contentString = await resultContent.ReadAsStringAsync();
            var jArray = JArray.Parse(contentString);

            foreach (var item in jArray)
            {
                var document = JsonConvert.DeserializeObject<T>(item.ToString());
                results.Add(document);
            }

            return results;
        }

        private async Task<T> GetDocumentFromResponse<T>(HttpContent resultContent) where T : class
        {
            var contentString = await resultContent.ReadAsStringAsync();
            var document = JsonConvert.DeserializeObject<T>(contentString);
            return document;
        }

        private async Task PostToBulkDocs(IEnumerable<object> commands)
        {
            var bulkUpdateUrl = $"{serverUrl}/databases/{databaseName}/bulk_docs";
            var serializedCommands = JsonConvert.SerializeObject(commands);
            using var result = await httpClient.PostAsync(bulkUpdateUrl, new StringContent(serializedCommands, Encoding.UTF8, "application/json"));
            result.EnsureSuccessStatusCode();
        }

        private Raven3BatchCommand GetDeleteCommand(string key)
        {
            return new Raven3BatchCommand
            {
                Key = key,
                Method = "DELETE",
                DebugMode = false
            };
        }
    }
}