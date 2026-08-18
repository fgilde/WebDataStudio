import { notifications } from "@mantine/notifications";

export interface JobOptions {
  title: string;
  /// Shown while the job runs; the toast stays until it finishes.
  message?: string;
  /// Present only when the job can actually be stopped — a cancel button that does nothing is
  /// worse than none.
  cancel?: () => void;
}

/// Wraps a long-running job in one toast that goes from running to done or failed. Export,
/// import, backup, restore and deep analyze all report through here so they look the same.
export async function runJob<T>(options: JobOptions, work: () => Promise<T>): Promise<T> {
  const id = `job-${options.title}-${performance.now()}`;

  notifications.show({
    id,
    title: options.title,
    message: options.message ?? "running…",
    loading: true,
    autoClose: false,
    withCloseButton: Boolean(options.cancel),
    onClose: options.cancel,
  });

  try {
    const result = await work();

    notifications.update({
      id,
      title: options.title,
      message: "done",
      color: "green",
      loading: false,
      autoClose: 3000,
      withCloseButton: true,
    });

    return result;
  } catch (error) {
    notifications.update({
      id,
      title: options.title,
      message: error instanceof Error ? error.message : String(error),
      color: "red",
      loading: false,
      // A failure stays on screen: it is the only place the message appears.
      autoClose: false,
      withCloseButton: true,
    });

    throw error;
  }
}
