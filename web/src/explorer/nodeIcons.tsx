import {
  IconBinaryTree, IconBolt, IconBox, IconBroadcast, IconColumns3, IconDatabase, IconEye, IconEyeCheck,
  IconFolder, IconFunction, IconHexagon, IconKey, IconLink, IconListNumbers, IconPuzzle, IconServer2,
  IconTable, IconUsers,
} from "@tabler/icons-react";

export function nodeIcon(kind: string) {
  const size = 15;
  switch (kind) {
    case "Database":
    case "Schema": return <IconDatabase size={size} />;
    case "Table": return <IconTable size={size} />;
    case "View": return <IconEye size={size} />;
    // A materialised view sits in the same folder as the plain ones, so it carries its own mark.
    case "MaterializedView": return <IconEyeCheck size={size} />;
    case "Function":
    case "Procedure": return <IconFunction size={size} />;
    case "Trigger": return <IconBolt size={size} />;
    case "Sequence": return <IconListNumbers size={size} />;
    case "Index": return <IconBinaryTree size={size} />;
    case "Column": return <IconColumns3 size={size} />;
    case "PrimaryKey": return <IconKey size={size} />;
    case "ForeignKey": return <IconLink size={size} />;
    case "Extension": return <IconPuzzle size={size} />;
    case "Role": return <IconUsers size={size} />;
    case "Tablespace": return <IconServer2 size={size} />;
    case "Publication": return <IconBroadcast size={size} />;
    case "Subscription": return <IconBroadcast size={size} />;
    case "Type": return <IconHexagon size={size} />;
    case "Domain": return <IconBox size={size} />;
    default: return <IconFolder size={size} />;
  }
}
