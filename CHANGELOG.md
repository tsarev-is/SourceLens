## [1.1.0] - 2026-06-13
* Hybrid dense + BM25/FTS5 retrieval fused via RRF.
* MMR diversity reranking and relevance-score thresholds.
* Dialog-history-aware query rewriting, plus LLM expansion of the first question before retrieval.
* Source collections: group books into named, colored groups; scope a session to a collection,
  an explicit set of documents, or a single source ("Only this" / "Search only this source").
* Explicit retrieval-state signal in the UI.
* Token-aware chunking (chunker v2): chunks are now bounded by the embedder's real token
  budget (via the shared tokenizer), so dense/non-Latin documents no longer overflow the
  512-token limit and lose their tail.

## [1.0.0] - 2026-06-12
* Initial implementation of the SourceLens knowledge RAG platform for students and researchers.