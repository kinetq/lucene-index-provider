using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.DocumentMapper.Attributes;
using Lucene.Net.DocumentMapper.Interfaces;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.IndexProvider.FilterBuilder;
using Lucene.Net.IndexProvider.Helpers;
using Lucene.Net.IndexProvider.Interfaces;
using Lucene.Net.IndexProvider.Models;
using Lucene.Net.Search;
using Microsoft.Extensions.Logging;
using Directory = Lucene.Net.Store.Directory;
using IndexFilter = Lucene.Net.IndexProvider.FilterBuilder.IndexFilter;
using Sort = Lucene.Net.IndexProvider.FilterBuilder.Sort;

namespace Lucene.Net.IndexProvider
{
    /// <summary>
    /// Represents the default implementation of an IIndexProvider, based on Lucene
    /// </summary>
    public class LuceneIndexProvider : IIndexProvider
    {
        private readonly IDocumentMapper _mapper;
        private readonly ILogger<LuceneIndexProvider> _logger;
        private readonly ILocalIndexPathFactory _localIndexPathFactory;
        private readonly IIndexSessionManager _sessionManager;
        private readonly IIndexConfigurationManager _configurationManager;
        private readonly IDirectoryManager _directoryManager;

        public LuceneIndexProvider(
            IDocumentMapper mapper,
            ILoggerFactory loggerFactory,
            ILocalIndexPathFactory localIndexPathFactory,
            IIndexSessionManager sessionManager,
            IIndexConfigurationManager configurationManager, 
            IDirectoryManager directoryManager)
        {
            _mapper = mapper;
            _localIndexPathFactory = localIndexPathFactory;
            _sessionManager = sessionManager;
            _configurationManager = configurationManager;
            _directoryManager = directoryManager;
            _logger = loggerFactory.CreateLogger<LuceneIndexProvider>();
        }

        private Directory GetDirectory(string indexName)
        {
            return _directoryManager.GetDirectory(indexName);
        }

        /// <summary>
        /// Gets a single document for the supplied key
        /// </summary>
        /// <param name="contentType"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        public IndexResult<object> GetDocumentById(Type contentType, string id)
        {
            IndexResult<object> indexResult = null;
            var luceneConfig = _configurationManager.GetConfiguration(contentType.Name);

            var luceneSession = _sessionManager.GetSessionFrom(contentType.Name);
            var indexSearcher = luceneSession.SearcherManager.Acquire();

            try
            {
                var query = new TermQuery(new Term(GetKeyName(contentType), id));
                var hits = indexSearcher.Search(query, luceneConfig.BatchSize);

                if (hits.ScoreDocs.Length == 0)
                {
                    return new IndexResult<object>
                    {
                        Hit = null,
                        Score = 0
                    };
                }

                var doc = indexSearcher.Doc(hits.ScoreDocs[0].Doc);
                var mappedDocument = _mapper.Map(doc, contentType);
                return new IndexResult<object>
                {
                    Hit = mappedDocument,
                    Score = hits.ScoreDocs[0].Score
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not get document for id {0} and content type", id, contentType.Name);
            }
            finally
            {
                luceneSession.SearcherManager.Release(indexSearcher);
            }

            return indexResult;
        }

        /// <summary>
        /// Gets a single document for the supplied key and casts it
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="id"></param>
        /// <returns></returns>
        public IndexResult<T> GetDocumentById<T>(string id)
        {
            var indexResult = GetDocumentById(typeof(T), id);
            return new IndexResult<T>
            {
                Hit = (T)indexResult.Hit,
                Score = indexResult.Score
            };
        }

        public Task<bool> CheckHealth<T>()
        {
            return Task.Run(async () =>
            {
                Type contentType = typeof(T);

                string localPath = _localIndexPathFactory.GetLocalIndexPath();
                string tempIndexPath = $"{contentType.Name}_{Guid.NewGuid()}";

                string tempIndexPathFull = Path.Combine(localPath, tempIndexPath);
                string indexFullPath = Path.Combine(localPath, contentType.Name);

                FileHelpers.CopyFilesRecursively(indexFullPath, tempIndexPathFull);
                var directory = GetDirectory(tempIndexPath);

                CheckIndex checkIndex = new CheckIndex(directory);
                var result = checkIndex.DoCheckIndex();

                await DeleteIndex(tempIndexPath);
                return result.Clean;
            });
        }

        private string GetKeyName(Type contentType)
        {
            var keyProperty =
                contentType.GetProperties()
                    .FirstOrDefault(x =>
                        x.GetCustomAttribute<SearchAttribute>() != null &&
                        x.GetCustomAttribute<SearchAttribute>().IsKey
                    );

            return keyProperty == null ? "Id" : keyProperty.Name;
        }

        /// <summary>
        /// Swaps a newly created index with an older one
        /// </summary>
        /// <param name="tempIndex"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public Task<bool> SwapIndex(string tempIndex, string index)
        {
            return Task.Run(async () =>
            {
                string localPath = _localIndexPathFactory.GetLocalIndexPath();
                _sessionManager.AddLock(index);
                try
                {
                    _sessionManager.CloseSession(tempIndex);
                    _sessionManager.CloseSession(index);

                    string tempIndexPath = Path.Combine(localPath, tempIndex);
                    string indexPath = Path.Combine(localPath, index);

                    if (!System.IO.Directory.Exists(tempIndexPath))
                    {
                        _logger.LogInformation("The index to be swapped {0} does not exist", index);
                        return false;
                    }

                    await DeleteIndex(index);
                    System.IO.Directory.Move(tempIndexPath, indexPath);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Could not swap index {index}");
                    return false;
                }
                finally
                {
                    _sessionManager.ReleaseLock(index);
                }
            });
        }

        /// <summary>
        /// Exposes a fluent api for searching against th index
        /// </summary>
        /// <returns></returns>
        public IndexFilterBuilder Search()
        {
            return new IndexFilterBuilder(this);
        }

        public async Task<IndexListResult<T>> GetByFilters<T>(IList<IndexFilter> filters, IList<Sort> sorts, int? page = null, int? pageSize = null)
        {
            var contentType = typeof(T);
            var listResult = await GetByFilters(filters, sorts, contentType, page, pageSize);

            IList<IndexResult<T>> indexResults = new List<IndexResult<T>>();
            foreach (var listResultItem in listResult.Hits)
            {
                indexResults.Add(new IndexResult<T>()
                {
                    Hit = (T)listResultItem.Hit,
                    Score = listResultItem.Score
                });
            }

            return new IndexListResult<T>
            {
                Count = listResult.Count,
                Hits = indexResults,
                MaxScore = listResult.MaxScore
            };
        }


        public async Task<IndexListResult> GetByFilters(IList<IndexFilter> filters, IList<Sort> sorts, Type contentType, int? page = null, int? pageSize = null)
        {
            List<IndexResult<object>> indexResults = new List<IndexResult<object>>();
            var luceneConfig = _configurationManager.GetConfiguration(contentType.Name);

            return await Task.Run(() =>
            {
                int count;
                float maxScore;

                var luceneSession = _sessionManager.GetSessionFrom(contentType.Name);
                var indexSearcher = luceneSession.SearcherManager.Acquire();

                try
                {
                    Query query = new MatchAllDocsQuery();
                    if (filters.Any())
                    {
                        query = new BooleanQuery();
                        foreach (var filter in filters)
                        {
                            ((BooleanQuery)query).Add(filter.Query, filter.OccurType);
                        }
                    }

                    var sort = new Search.Sort();
                    if (sorts.Any())
                    {
                        sort.SetSort(sorts.Select(x => x.SortField).ToArray());
                    }
                    else
                    {
                        sort.SetSort(SortField.FIELD_SCORE);
                    }

                    var rewrittenSort = sort.Rewrite(indexSearcher);
                    var hits = indexSearcher.Search(query, luceneConfig.BatchSize, rewrittenSort);
                    count = hits.TotalHits;
                    maxScore = hits.MaxScore;

                    if (page.HasValue && pageSize.HasValue)
                    {
                        int first = (int)((page - 1) * pageSize);
                        int last = (int)(page * pageSize);

                        for (int i = first; i < last && i < hits.TotalHits; i++)
                        {
                            Document doc = indexSearcher.Doc(hits.ScoreDocs[i].Doc);
                            var contentItem = _mapper.Map(doc, contentType);
                            indexResults.Add(new IndexResult<object>
                            {
                                Hit = contentItem,
                                Score = hits.ScoreDocs[i].Score
                            });
                        }
                    }
                    else
                    {
                        for (int i = 0; i < hits.TotalHits; i++)
                        {
                            Document doc = indexSearcher.Doc(hits.ScoreDocs[i].Doc);
                            var contentItem = _mapper.Map(doc, contentType);
                            indexResults.Add(new IndexResult<object>
                            {
                                Hit = contentItem,
                                Score = hits.ScoreDocs[i].Score
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Could not perform search against index: {contentType.Name}");
                    return new IndexListResult();
                }
                finally
                {
                    luceneSession.SearcherManager.Release(indexSearcher);
                }

                return new IndexListResult
                {
                    Hits = indexResults,
                    Count = count,
                    MaxScore = maxScore
                };
            });
        }

        public async Task<bool> Update(IList<object> contentItems)
        {
            foreach (var contentItem in contentItems)
            {
                var key = GetKeyName(contentItem.GetType());
                var idPropInfo = contentItem.GetType().GetProperties().First(x => x.Name.Equals(key));
                var id = idPropInfo.GetValue(contentItem).ToString();

                var result = await Update(contentItem, id);
                if (!result)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Updates a single document based on the key supplied
        /// </summary>
        /// <param name="contentItem"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        public Task<bool> Update(object contentItem, string id)
        {
            string indexName = contentItem.GetType().Name;
            return Task.Run(() =>
            {
                try
                {
                    var luceneSession = _sessionManager.GetSessionFrom(indexName);
                    var writer = luceneSession.Writer;
                    var doc = _mapper.Map(contentItem);
                    writer.UpdateDocument(new Term(GetKeyName(contentItem.GetType()), id), doc);

                    if (writer.HasDeletions())
                    {
                        writer.ForceMergeDeletes();
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not update content item {0}", id);
                    return false;
                }
            });
        }

        /// <summary>
        /// Creates the index for a given type if it doesn't exist
        /// </summary>
        /// <param name="contentType"></param>
        /// <returns></returns>
        public Task CreateIndexIfNotExists(Type contentType)
        {
            return CreateIndexIfNotExists(contentType.Name);
        }

        /// <summary>
        /// Creates the index for a given type if it doesn't exist
        /// </summary>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public Task CreateIndexIfNotExists(string indexName)
        {
            var luceneConfig = _configurationManager.GetConfiguration(indexName);
            bool exists = DirectoryReader.IndexExists(GetDirectory(indexName));
            if (exists) return Task.FromResult(0);

            return Task.Run(() =>
            {
                using (var analyzer = new StandardAnalyzer(luceneConfig.LuceneVersion))
                {
                    var config = new IndexWriterConfig(luceneConfig.LuceneVersion, analyzer);
                    using (new IndexWriter(GetDirectory(indexName), config))
                    {
                    }
                }
            });
        }

        /// <summary>
        /// Deletes an index for the given type
        /// </summary>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public Task DeleteIndex(string indexName)
        {
            return Task.Run(() =>
            {
                string localPath = _localIndexPathFactory.GetLocalIndexPath();
                var directory = new DirectoryInfo(Path.Combine(localPath, indexName));
                if (!directory.Exists)
                {
                    _logger.LogWarning($"Could not find directory {indexName} to delete.");
                    return;
                }

                directory.Delete(true);
            });
        }

        /// <summary>
        /// Takes a list of objects and stores them to the same index
        /// </summary>
        /// <param name="contentItems"></param>
        /// <param name="contentType"></param>
        /// <returns></returns>
        public Task Store(IList<object> contentItems, Type contentType)
        {
            return Store(contentItems, contentType.Name);
        }

        /// <summary>
        /// Allowing to store documents into indexes with no type
        /// </summary>
        /// <param name="contentItems"></param>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public Task Store(IList<object> contentItems, string indexName)
        {
            contentItems = contentItems.ToArray();

            if (!contentItems.Any())
            {
                return Task.FromResult(0);
            }

            return Task.Run(() =>
            {
                var luceneSession = _sessionManager.GetSessionFrom(indexName);
                var writer = luceneSession.Writer;
                foreach (var contentItem in contentItems)
                {
                    try
                    {
                        var doc = _mapper.Map(contentItem);
                        writer.AddDocument(doc);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, $"Could not add document to index");
                    }
                }
            });
        }

        /// <summary>
        /// Takes a list of objects and stores them in the same index
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="contentItems"></param>
        /// <returns></returns>
        public Task Store<T>(IList<T> contentItems)
        {
            return Store(contentItems.Cast<object>().ToList(), typeof(T));
        }

        /// <summary>
        /// Deletes a document for the given key
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="documentId"></param>
        /// <returns></returns>
        public Task Delete<T>(string documentId)
        {
            return Delete(typeof(T), documentId);
        }

        /// <summary>
        /// Deletes a document for the given key
        /// </summary>
        /// <param name="contentType"></param>
        /// <param name="documentId"></param>
        /// <returns></returns>
        public Task Delete(Type contentType, string documentId)
        {
            return Task.Run(() =>
            {
                var luceneSession = _sessionManager.GetSessionFrom(contentType.Name);
                var writer = luceneSession.Writer;
                writer.DeleteDocuments(new Term(GetKeyName(contentType), documentId));
                if (writer.HasDeletions())
                {
                    writer.ForceMergeDeletes();
                }
            });
        }
    }
}
