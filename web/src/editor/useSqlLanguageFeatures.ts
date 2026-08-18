import { useEffect } from "react";
import * as monaco from "monaco-editor";
import { completionContext, SQL_KEYWORDS } from "./completion";
import { schemaCache } from "./schemaCache";
import { allSnippets, type Snippet } from "./snippets";
import type { DialectId } from "../sql/splitStatements";

// Completion, hover and go-to-definition, all scoped to the connection the tab is bound to.
// Registered per connection and disposed on change, so two tabs on two databases never mix schemas.
export function useSqlLanguageFeatures(
  connectionId: string,
  _dialect: DialectId,
  onOpenObject?: (ref: string) => void,
  userSnippets: Snippet[] = [],
) {
  useEffect(() => {
    const completion = monaco.languages.registerCompletionItemProvider("sql", {
      triggerCharacters: [".", " "],
      provideCompletionItems: async (model, position) => {
        const offset = model.getOffsetAt(position);
        const word = model.getWordUntilPosition(position);
        const range = new monaco.Range(
          position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);

        const item = (label: string, kind: monaco.languages.CompletionItemKind, detail?: string) =>
          ({ label, kind, insertText: label, range, detail });

        const context = completionContext(model.getValue(), offset);

        if (context.kind === "columns") {
          const columns = await schemaCache.columns(connectionId, context.table);
          return {
            suggestions: columns.map(c =>
              item(c, monaco.languages.CompletionItemKind.Field, context.table)),
          };
        }

        const tables = await schemaCache.tables(connectionId);
        const tableItems = tables.map(t =>
          item(t.name, monaco.languages.CompletionItemKind.Struct, t.schema));
        if (context.kind === "tables") return { suggestions: tableItems };

        // Snippets come first: a three-letter prefix like `sel` should not be buried under
        // every table whose name starts with the same letters.
        const snippetItems = allSnippets(userSnippets).map(snippet => ({
          label: snippet.prefix,
          kind: monaco.languages.CompletionItemKind.Snippet,
          insertText: snippet.body,
          insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
          detail: snippet.label,
          documentation: snippet.description,
          range,
        }));

        return {
          suggestions: [
            ...snippetItems,
            ...tableItems,
            ...SQL_KEYWORDS.map(k => item(k, monaco.languages.CompletionItemKind.Keyword)),
          ],
        };
      },
    });

    const hover = monaco.languages.registerHoverProvider("sql", {
      provideHover: async (model, position) => {
        const word = model.getWordAtPosition(position);
        if (!word) return null;

        const columns = await schemaCache.columns(connectionId, word.word);
        if (columns.length === 0) return null;

        return {
          contents: [
            { value: `**${word.word}**` },
            { value: columns.map(c => `- ${c}`).join("\n") },
          ],
        };
      },
    });

    const definition = monaco.languages.registerDefinitionProvider("sql", {
      provideDefinition: async (model, position) => {
        const word = model.getWordAtPosition(position);
        if (!word || !onOpenObject) return null;

        const tables = await schemaCache.tables(connectionId);
        const table = tables.find(t => t.name.toLowerCase() === word.word.toLowerCase());
        // Opening the object in the explorer is more useful than jumping inside the text buffer.
        if (table) onOpenObject(table.ref);
        return null;
      },
    });

    return () => { completion.dispose(); hover.dispose(); definition.dispose(); };
    // The snippet list is compared by content: a new array of the same snippets must not
    // re-register the providers on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectionId, onOpenObject, JSON.stringify(userSnippets)]);
}
