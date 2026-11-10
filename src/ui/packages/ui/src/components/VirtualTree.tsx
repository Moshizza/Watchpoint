import { useT } from '@watchpoint/i18n';
import { ChevronDown, ChevronLeft, ChevronRight } from 'lucide-react';
import { type KeyboardEvent, useCallback, useRef, useState } from 'react';
import { cn } from '../utils.ts';

export interface TreeNode<T = unknown> {
  id: string;
  label: string;
  data?: T;
  hasChildren?: boolean;
  children?: TreeNode<T>[];
  badge?: string;
}

export interface VirtualTreeProps<T = unknown> {
  nodes: TreeNode<T>[];
  onLoadChildren?: (node: TreeNode<T>) => Promise<TreeNode<T>[]>;
  onSelectNode?: (node: TreeNode<T>) => void;
  selectedId?: string;
  isRtl?: boolean;
  className?: string;
}

interface FlatNode<T> {
  node: TreeNode<T>;
  depth: number;
  expanded: boolean;
  loading: boolean;
}

export function VirtualTree<T = unknown>({
  nodes,
  onLoadChildren,
  onSelectNode,
  selectedId,
  isRtl = false,
  className,
}: VirtualTreeProps<T>) {
  const t = useT('app');
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());
  const [childrenMap, setChildrenMap] = useState<Map<string, TreeNode<T>[]>>(new Map());
  const [loadingIds, setLoadingIds] = useState<Set<string>>(new Set());
  const [focusedId, setFocusedId] = useState<string | null>(nodes[0]?.id ?? null);
  const containerRef = useRef<HTMLDivElement>(null);
  const typeaheadRef = useRef<string>('');
  const typeaheadTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const toggleExpand = useCallback(
    async (node: TreeNode<T>) => {
      const isExpanded = expandedIds.has(node.id);
      if (isExpanded) {
        setExpandedIds((prev) => {
          const next = new Set(prev);
          next.delete(node.id);
          return next;
        });
      } else {
        if (node.hasChildren && !childrenMap.has(node.id) && onLoadChildren) {
          setLoadingIds((prev) => new Set(prev).add(node.id));
          try {
            const children = await onLoadChildren(node);
            setChildrenMap((prev) => new Map(prev).set(node.id, children));
          } finally {
            setLoadingIds((prev) => {
              const next = new Set(prev);
              next.delete(node.id);
              return next;
            });
          }
        }
        setExpandedIds((prev) => new Set(prev).add(node.id));
      }
    },
    [expandedIds, childrenMap, onLoadChildren],
  );

  const flatten = (items: TreeNode<T>[], depth = 0): FlatNode<T>[] => {
    const list: FlatNode<T>[] = [];
    for (const item of items) {
      const expanded = expandedIds.has(item.id);
      const loading = loadingIds.has(item.id);
      list.push({ node: item, depth, expanded, loading });
      if (expanded) {
        const dynamicChildren = childrenMap.get(item.id);
        const children = dynamicChildren ?? item.children ?? [];
        list.push(...flatten(children, depth + 1));
      }
    }
    return list;
  };

  const flatList = flatten(nodes);

  const handleKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const currentIndex = flatList.findIndex((item) => item.node.id === focusedId);
    if (currentIndex === -1 && flatList.length > 0) {
      setFocusedId(flatList[0]?.node.id ?? null);
      return;
    }

    const currentItem = flatList[currentIndex];
    if (!currentItem) return;

    const expandKey = isRtl ? 'ArrowLeft' : 'ArrowRight';
    const collapseKey = isRtl ? 'ArrowRight' : 'ArrowLeft';

    switch (e.key) {
      case 'ArrowDown': {
        e.preventDefault();
        const next = flatList[currentIndex + 1];
        if (next) setFocusedId(next.node.id);
        break;
      }
      case 'ArrowUp': {
        e.preventDefault();
        const prev = flatList[currentIndex - 1];
        if (prev) setFocusedId(prev.node.id);
        break;
      }
      case expandKey: {
        e.preventDefault();
        if (currentItem.node.hasChildren || currentItem.node.children?.length) {
          if (!currentItem.expanded) {
            void toggleExpand(currentItem.node);
          } else {
            const next = flatList[currentIndex + 1];
            if (next && next.depth > currentItem.depth) {
              setFocusedId(next.node.id);
            }
          }
        }
        break;
      }
      case collapseKey: {
        e.preventDefault();
        if (currentItem.expanded) {
          void toggleExpand(currentItem.node);
        } else if (currentItem.depth > 0) {
          for (let i = currentIndex - 1; i >= 0; i--) {
            const candidate = flatList[i];
            if (candidate && candidate.depth < currentItem.depth) {
              setFocusedId(candidate.node.id);
              break;
            }
          }
        }
        break;
      }
      case 'Home': {
        e.preventDefault();
        if (flatList.length > 0) setFocusedId(flatList[0]?.node.id ?? null);
        break;
      }
      case 'End': {
        e.preventDefault();
        if (flatList.length > 0) setFocusedId(flatList[flatList.length - 1]?.node.id ?? null);
        break;
      }
      case 'Enter':
      case ' ': {
        e.preventDefault();
        onSelectNode?.(currentItem.node);
        break;
      }
      default: {
        if (e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey) {
          if (typeaheadTimerRef.current) clearTimeout(typeaheadTimerRef.current);
          typeaheadRef.current += e.key.toLowerCase();
          typeaheadTimerRef.current = setTimeout(() => {
            typeaheadRef.current = '';
          }, 500);

          const match = flatList.find((item) => item.node.label.toLowerCase().startsWith(typeaheadRef.current));
          if (match) {
            setFocusedId(match.node.id);
          }
        }
        break;
      }
    }
  };

  return (
    <div
      ref={containerRef}
      role="tree"
      tabIndex={0}
      data-testid="virtual-tree"
      onKeyDown={handleKeyDown}
      className={cn(
        'select-none outline-hidden overflow-y-auto max-h-[500px] border border-wp-border rounded p-1 bg-wp-surface',
        className,
      )}
    >
      {flatList.map(({ node, depth, expanded, loading }) => {
        const isSelected = selectedId === node.id;
        const isFocused = focusedId === node.id;
        const hasChildren = Boolean(node.hasChildren || node.children?.length);

        return (
          <div
            key={node.id}
            role="treeitem"
            aria-expanded={hasChildren ? expanded : undefined}
            aria-selected={isSelected}
            tabIndex={isFocused ? 0 : -1}
            data-testid={`tree-node-${node.id}`}
            onClick={() => {
              setFocusedId(node.id);
              onSelectNode?.(node);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                setFocusedId(node.id);
                onSelectNode?.(node);
              }
            }}
            className={cn(
              'flex items-center gap-1.5 py-1 px-2 rounded cursor-pointer text-sm transition-colors',
              isSelected && 'bg-wp-surface-raised font-medium text-wp-text',
              isFocused && 'ring-1 ring-wp-border',
              !isSelected && 'hover:bg-wp-surface-raised/50 text-wp-text',
            )}
            style={{ paddingInlineStart: `${depth * 1.25 + 0.5}rem` }}
          >
            {hasChildren ? (
              <button
                type="button"
                tabIndex={-1}
                aria-label={expanded ? t('tree.collapse') : t('tree.expand')}
                onClick={(e) => {
                  e.stopPropagation();
                  void toggleExpand(node);
                }}
                className="w-4 h-4 flex items-center justify-center p-0 text-wp-text-muted hover:text-wp-text cursor-pointer"
              >
                {loading ? (
                  <span className="w-3 h-3 border-2 border-wp-border border-t-wp-text rounded-full animate-spin" />
                ) : expanded ? (
                  <ChevronDown className="w-3.5 h-3.5" aria-hidden="true" />
                ) : isRtl ? (
                  <ChevronLeft className="w-3.5 h-3.5" aria-hidden="true" />
                ) : (
                  <ChevronRight className="w-3.5 h-3.5" aria-hidden="true" />
                )}
              </button>
            ) : (
              <span className="w-4 h-4 shrink-0" />
            )}

            <span className="truncate flex-1">{node.label}</span>

            {node.badge && (
              <span className="ms-auto text-xs px-1.5 py-0.5 rounded bg-wp-surface-raised text-wp-text-muted">
                {node.badge}
              </span>
            )}
          </div>
        );
      })}
    </div>
  );
}
