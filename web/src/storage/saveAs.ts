/// Saving a file the studio can fetch, to wherever the person wants it.
///
/// Two paths, on purpose. A browser with the File System Access API asks where the file should go and
/// streams into it, which is what "Save as…" means and what makes a 4 GB Parquet possible without
/// holding it in memory. Everything else gets the download it already had: the browser decides the
/// folder and the name is a suggestion.
export interface SavePicker {
  showSaveFilePicker?: (options: {
    suggestedName?: string;
    types?: { description: string; accept: Record<string, string[]> }[];
  }) => Promise<FileSystemFileHandle>;
}

/// What happened, so a caller can say it: `saved` is a file on disk, `downloaded` went through the
/// browser's own download, `cancelled` is the person closing the dialog.
export type SaveOutcome = "saved" | "downloaded" | "cancelled";

/// The extension of a name, as the picker wants it — `.parquet`, or empty where there is none.
export function extensionOf(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot > 0 && dot < name.length - 1 ? name.slice(dot).toLowerCase() : "";
}

export async function saveAs(url: string, name: string, options: {
  /// What the file holds, where the server said. Only used to describe the type in the picker.
  contentType?: string | null;
  /// Injected in tests; the browser's own object otherwise.
  picker?: SavePicker;
  fetcher?: typeof fetch;
} = {}): Promise<SaveOutcome> {
  const picker = options.picker ?? (window as unknown as SavePicker);
  const fetcher = options.fetcher ?? fetch;

  if (typeof picker.showSaveFilePicker !== "function") {
    download(url, name);
    return "downloaded";
  }

  let handle: FileSystemFileHandle;

  try {
    const extension = extensionOf(name);

    handle = await picker.showSaveFilePicker({
      suggestedName: name,
      types: extension
        ? [{
            description: options.contentType ?? "File",
            accept: { [options.contentType ?? "application/octet-stream"]: [extension] },
          }]
        : undefined,
    });
  } catch {
    // The person closed the dialog, or the browser refused the picker (an iframe without the
    // permission). Neither is an error worth showing, and neither is a reason to download something
    // nobody asked to save.
    return "cancelled";
  }

  const response = await fetcher(url);
  if (!response.ok) throw new Error(`the file could not be read (${response.status})`);

  const writable = await handle.createWritable();

  // Streamed rather than buffered where the browser can: a download that fits in memory is not the
  // interesting case.
  if (response.body && typeof response.body.pipeTo === "function")
    await response.body.pipeTo(writable);
  else {
    await writable.write(await response.blob());
    await writable.close();
  }

  return "saved";
}

function download(url: string, name: string) {
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
}
