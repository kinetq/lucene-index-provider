using System;
using System.Collections.Generic;
using Lucene.Net.Util;

namespace Lucene.Net.IndexProvider.Models
{
    public class LuceneConfig
    {
        public LuceneVersion LuceneVersion { get; set; }
        public int BatchSize { get; set; }
        public IList<string> Indexes { get; set; }
        public bool ReadOnly { get; set; }
        public int WriteLockTimeout { get; set; } = 30000;

        /// <summary>
        /// Number of parallel shard writers to use when indexing documents.
        /// When greater than 1, documents are distributed across shard indexes
        /// and merged back into the main index after writing. Defaults to 1 (single writer).
        /// </summary>
        public int WriterCount { get; set; } = 1;
    }
}