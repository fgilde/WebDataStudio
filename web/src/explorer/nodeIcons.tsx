import {
  IconBinaryTree, IconBolt, IconBox, IconBroadcast, IconColumns3, IconDatabase, IconEye, IconEyeCheck,
  IconFolder, IconFunction, IconHexagon, IconKey, IconLink, IconListNumbers, IconPuzzle, IconServer2,
  IconTable, IconUsers, IconBucket, IconDots, IconFile, IconFolderOpen,
} from "@tabler/icons-react";

export function nodeIcon(kind: string) {
  const size = 15;
  // Never squeezed by a long name beside it: the name is what gives way, with an ellipsis.
  const style = { flexShrink: 0 };
  switch (kind) {
    case "Database":
    case "Schema": return <IconDatabase size={size} style={style} />;
    case "Table": return <IconTable size={size} style={style} />;
    case "View": return <IconEye size={size} style={style} />;
    // A materialised view sits in the same folder as the plain ones, so it carries its own mark.
    case "MaterializedView": return <IconEyeCheck size={size} style={style} />;
    case "Function":
    case "Procedure": return <IconFunction size={size} style={style} />;
    case "Trigger": return <IconBolt size={size} style={style} />;
    case "Sequence": return <IconListNumbers size={size} style={style} />;
    case "Index": return <IconBinaryTree size={size} style={style} />;
    case "Column": return <IconColumns3 size={size} style={style} />;
    case "PrimaryKey": return <IconKey size={size} style={style} />;
    case "ForeignKey": return <IconLink size={size} style={style} />;
    case "Extension": return <IconPuzzle size={size} style={style} />;
    case "Role": return <IconUsers size={size} style={style} />;
    case "Tablespace": return <IconServer2 size={size} style={style} />;
    case "Publication": return <IconBroadcast size={size} style={style} />;
    case "Subscription": return <IconBroadcast size={size} style={style} />;
    case "Type": return <IconHexagon size={size} style={style} />;
    case "Domain": return <IconBox size={size} style={style} />;
    // Object storage: a container, a prefix inside it, an object, and the row that fetches the next
    // page of a long listing.
    case "Container": return <IconBucket size={size} style={style} />;
    case "Prefix": return <IconFolderOpen size={size} style={style} />;
    case "StorageObject": return <IconFile size={size} style={style} />;
    case "StorageMore": return <IconDots size={size} style={style} />;
    default: return <IconFolder size={size} style={style} />;
  }
}
