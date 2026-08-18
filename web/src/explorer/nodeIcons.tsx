import {
  IconBinaryTree, IconBolt, IconDatabase, IconEye, IconFolder,
  IconFunction, IconListNumbers, IconTable,
} from "@tabler/icons-react";

export function nodeIcon(kind: string) {
  const size = 15;
  switch (kind) {
    case "Database":
    case "Schema": return <IconDatabase size={size} />;
    case "Table": return <IconTable size={size} />;
    case "View":
    case "MaterializedView": return <IconEye size={size} />;
    case "Function":
    case "Procedure": return <IconFunction size={size} />;
    case "Trigger": return <IconBolt size={size} />;
    case "Sequence": return <IconListNumbers size={size} />;
    case "Index": return <IconBinaryTree size={size} />;
    default: return <IconFolder size={size} />;
  }
}
