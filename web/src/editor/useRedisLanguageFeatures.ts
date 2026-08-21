import { useEffect } from "react";
import * as monaco from "monaco-editor";
import { commandIndex, describe, suggest } from "../redis/commandHelp";

/// Completion and hover for the Redis console, from what the server itself reports — a server with
/// modules has commands no baked-in list knows about.
///
/// The console is a plaintext model, so the provider is registered for plaintext and scoped to the
/// model of this tab: two tabs on two servers must not offer each other's commands.
export function useRedisLanguageFeatures(connectionId: string, enabled: boolean) {
  useEffect(() => {
    if (!enabled) return;

    let index = new Map<string, ReturnType<typeof suggest>[number]>();
    void commandIndex(connectionId).then(loaded => { index = loaded; });

    const completion = monaco.languages.registerCompletionItemProvider("plaintext", {
      provideCompletionItems: (model, position) => {
        const line = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
        // Only the first word of a line is a command; the rest are keys and arguments, which the
        // studio has no business guessing.
        if (/\s/.test(line.trim())) return { suggestions: [] };

        const word = model.getWordUntilPosition(position);
        const range = new monaco.Range(
          position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);

        return {
          suggestions: suggest(index, line.trim()).map(help => ({
            label: help.name,
            kind: monaco.languages.CompletionItemKind.Function,
            insertText: help.name,
            detail: help.group || undefined,
            documentation: describe(help),
            range,
          })),
        };
      },
    });

    const hover = monaco.languages.registerHoverProvider("plaintext", {
      provideHover: (model, position) => {
        const word = model.getWordAtPosition(position);
        if (!word) return null;

        const help = index.get(word.word.toUpperCase());
        if (!help) return null;

        return {
          contents: [{ value: `**${help.name}**` }, { value: describe(help) }],
        };
      },
    });

    return () => { completion.dispose(); hover.dispose(); };
  }, [connectionId, enabled]);
}
