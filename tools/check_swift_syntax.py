"""Portable Swift syntax check. This is NOT a substitute for swift build/test."""
from pathlib import Path
import sys
from tree_sitter import Language, Parser
import tree_sitter_swift


def main():
    parser = Parser(Language(tree_sitter_swift.language()))
    root = Path(__file__).resolve().parents[1] / "macos"
    failures = []
    files = [path for path in root.rglob("*.swift") if ".build" not in path.parts]
    files.append(root.parent / "tools" / "check_bundle.swift")
    for path in files:
        tree = parser.parse(path.read_bytes())
        if tree.root_node.has_error:
            nodes = [tree.root_node]
            while nodes:
                node = nodes.pop()
                if node.type == "ERROR" or node.is_missing:
                    failures.append(f"{path.relative_to(root.parent)}:{node.start_point.row + 1}: {node.type}")
                nodes.extend(node.children)
    if failures:
        print("\n".join(failures), file=sys.stderr)
        return 1
    print(f"Swift syntax: {len(files)} files passed (native type checking still requires macOS)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
