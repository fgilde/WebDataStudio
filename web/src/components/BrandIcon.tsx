import { useEffect, useState } from "react";

/// The icon this studio ships. Also the fallback for a `WDS_ICON` that does not resolve.
export const SHIPPED_ICON = "/brand/icon.svg";

/// The studio's icon, which a deployment may replace with `WDS_ICON`.
///
/// A value that does not load falls back to ours rather than leaving a broken image: the setting is
/// a path or a URL somebody typed, and the whole point of it is the first impression.
export function BrandIcon({ src, size, alt = "", style }: {
  src?: string | null;
  size: number;
  alt?: string;
  style?: React.CSSProperties;
}) {
  const wanted = src ?? SHIPPED_ICON;
  const [showing, setShowing] = useState(wanted);

  useEffect(() => { setShowing(wanted); }, [wanted]);

  return (
    <img src={showing} alt={alt} width={size} height={size}
      onError={() => setShowing(SHIPPED_ICON)}
      style={{ display: "block", objectFit: "contain", ...style }} />
  );
}
