# Optional assistance

Off by default, and off completely: without `WDS_ASSIST_ENDPOINT` there is no button, no call and
nothing in the UI. `GET /api/health` reports `assist: false`, which is how the studio itself knows.

## Turning it on

| Variable | Meaning |
|---|---|
| `WDS_ASSIST_ENDPOINT` | an OpenAI-compatible chat-completions URL; setting it enables the feature |
| `WDS_ASSIST_KEY` | sent as `Authorization: Bearer …`; omit it for an endpoint that needs none |
| `WDS_ASSIST_MODEL` | model name, default `gpt-4o-mini` |

```bash
WDS_ASSIST_ENDPOINT=https://api.openai.com/v1/chat/completions
WDS_ASSIST_KEY=sk-…
WDS_ASSIST_MODEL=gpt-4o-mini
```

A local endpoint works the same way — Ollama, llama.cpp, vLLM and LM Studio all speak this shape —
which is the way to have the feature without anything leaving the building.

### From an Aspire app host

The [Nextended.Aspire.Hosting.WebDataStudio](https://www.nuget.org/packages/Nextended.Aspire.Hosting.WebDataStudio)
package wires it to a model server in the same stack, so the conversation never leaves the machine:

```csharp
var ollama = builder.AddOllama("ollama").WithDataVolume();
ollama.AddModel("llama3.2");

builder.AddWebDataStudio()
    .WithReference(shop)
    .WithOllamaAssistant(ollama, "llama3.2");
```

`WithLocalAiAssistant(localai, "qwen3-8b")` does the same for LocalAI, and
`WithAssistant(url, model, key)` for a hosted model.

## What it does

The sparkle in the query toolbar opens one dialog with two actions:

- **Explain the statement** — what it does, what it reads, what would make it slow or wrong.
- **Draft SQL** — a statement from a question in prose.

## What leaves the machine

Exactly this:

- the statement in the editor, or the question you typed;
- **only with the switch on**, a summary of the connection's schema: table and column names with
  their types, capped at 60 tables and 40 columns each.

Never a row of data. Not a sample, not a value, not a masked one. A column called `secret_token`
travels as a name when the schema is sent, because that is what a schema is — its contents do not.

## What it cannot do

Nothing it returns is executed. A suggested statement is put into the editor when you ask for it,
and from there it goes through the same run, the same preview and the same read-only checks as
anything typed by hand. There is no path from a reply to a write.

## Leaving it off

Do not set `WDS_ASSIST_ENDPOINT`. The endpoints answer `501` — the route exists but the feature is
not configured, which is a more useful answer than `404` when you are trying to work out what a
deployment has. Nothing is sent anywhere, because there is nowhere to send it.
