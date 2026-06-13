{dialog-history}

The user is in an ongoing conversation. Rewrite their latest message into a single, self-contained search query for a document-retrieval system over the user's library.

Rules:
- Resolve pronouns and references ("he", "that", "what happened next") using DIALOG_HISTORY so the query stands on its own.
- Keep proper names, terminology, numbers and quotes verbatim — they matter most for search.
- Output ONLY the rewritten query on a single line. No quotes, no explanation, no prefix.

Latest message:
"""{question}"""
