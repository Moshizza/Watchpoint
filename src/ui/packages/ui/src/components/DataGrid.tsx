import {
  type ColumnDef,
  flexRender,
  getCoreRowModel,
  type RowSelectionState,
  type SortingState,
  type TableOptions,
  useReactTable,
} from '@tanstack/react-table';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useT } from '@watchpoint/i18n';
import { ArrowDown, ArrowUp, ArrowUpDown, RefreshCw } from 'lucide-react';
import { type KeyboardEvent, useEffect, useRef, useState } from 'react';
import { cn } from '../utils.ts';

export interface DataGridProps<TData> {
  data: TData[];
  columns: ColumnDef<TData, unknown>[];
  totalCount?: number;
  isLoading?: boolean;
  hasNextPage?: boolean;
  onFetchNextPage?: () => void;
  onSortingChange?: (sorting: SortingState) => void;
  onRowSelectionChange?: (selectedRows: TData[]) => void;
  getRowId?: (row: TData, index: number) => string;
  className?: string;
  rowHeight?: number;
}

export function DataGrid<TData>({
  data,
  columns,
  totalCount,
  isLoading = false,
  hasNextPage = false,
  onFetchNextPage,
  onSortingChange,
  onRowSelectionChange,
  getRowId,
  className,
  rowHeight = 36,
}: DataGridProps<TData>) {
  const t = useT('app');
  const [sorting, setSorting] = useState<SortingState>([]);
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({});
  const [focusedRowIndex, setFocusedRowIndex] = useState<number>(0);
  const tableContainerRef = useRef<HTMLDivElement>(null);

  const tableOptions: TableOptions<TData> = {
    data,
    columns,
    state: { sorting, rowSelection },
    enableRowSelection: true,
    onSortingChange: (updater) => {
      const nextSorting = typeof updater === 'function' ? updater(sorting) : updater;
      setSorting(nextSorting);
      onSortingChange?.(nextSorting);
    },
    onRowSelectionChange: (updater) => {
      const nextSelection = typeof updater === 'function' ? updater(rowSelection) : updater;
      setRowSelection(nextSelection);
    },
    getCoreRowModel: getCoreRowModel(),
  };

  if (getRowId) {
    tableOptions.getRowId = getRowId;
  }

  const table = useReactTable(tableOptions);

  const { rows } = table.getRowModel();

  useEffect(() => {
    if (onRowSelectionChange) {
      const selected = table.getSelectedRowModel().rows.map((r) => r.original);
      onRowSelectionChange(selected);
    }
  }, [rowSelection, table, onRowSelectionChange]);

  const rowVirtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => tableContainerRef.current,
    estimateSize: () => rowHeight,
    overscan: 10,
  });

  const virtualItems = rowVirtualizer.getVirtualItems();
  const totalVirtualHeight = rowVirtualizer.getTotalSize();

  useEffect(() => {
    const lastItem = virtualItems[virtualItems.length - 1];
    if (!lastItem) return;

    if (lastItem.index >= rows.length - 1 && hasNextPage && !isLoading && onFetchNextPage) {
      onFetchNextPage();
    }
  }, [virtualItems, rows.length, hasNextPage, isLoading, onFetchNextPage]);

  const handleKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (rows.length === 0) return;

    switch (e.key) {
      case 'ArrowDown': {
        e.preventDefault();
        setFocusedRowIndex((prev) => {
          const next = Math.min(prev + 1, rows.length - 1);
          rowVirtualizer.scrollToIndex(next);
          return next;
        });
        break;
      }
      case 'ArrowUp': {
        e.preventDefault();
        setFocusedRowIndex((prev) => {
          const next = Math.max(prev - 1, 0);
          rowVirtualizer.scrollToIndex(next);
          return next;
        });
        break;
      }
      case ' ': {
        e.preventDefault();
        const currentRow = rows[focusedRowIndex];
        if (currentRow) {
          currentRow.toggleSelected();
        }
        break;
      }
      case 'Home': {
        e.preventDefault();
        setFocusedRowIndex(0);
        rowVirtualizer.scrollToIndex(0);
        break;
      }
      case 'End': {
        e.preventDefault();
        setFocusedRowIndex(rows.length - 1);
        rowVirtualizer.scrollToIndex(rows.length - 1);
        break;
      }
    }
  };

  return (
    <div
      ref={tableContainerRef}
      tabIndex={0}
      role="grid"
      aria-rowcount={totalCount ?? rows.length}
      data-testid="data-grid"
      onKeyDown={handleKeyDown}
      className={cn(
        'w-full h-full overflow-auto border border-wp-border rounded bg-wp-surface text-wp-text outline-hidden',
        className,
      )}
    >
      <table className="w-full border-collapse text-sm">
        <thead className="sticky top-0 z-10 bg-wp-surface-raised border-b border-wp-border">
          {table.getHeaderGroups().map((headerGroup) => (
            <tr key={headerGroup.id}>
              {headerGroup.headers.map((header) => {
                const canSort = header.column.getCanSort();
                const isSorted = header.column.getIsSorted();

                return (
                  <th
                    key={header.id}
                    scope="col"
                    aria-sort={isSorted ? (isSorted === 'desc' ? 'descending' : 'ascending') : 'none'}
                    style={{ width: header.getSize() !== 150 ? header.getSize() : undefined }}
                    onClick={header.column.getToggleSortingHandler()}
                    className={cn(
                      'py-2 px-3 text-start font-medium select-none',
                      canSort && 'cursor-pointer hover:bg-wp-border/30',
                    )}
                  >
                    <div className="flex items-center gap-1">
                      {flexRender(header.column.columnDef.header, header.getContext())}
                      {canSort && (
                        <span className="text-wp-text-muted">
                          {isSorted === 'asc' ? (
                            <ArrowUp className="w-3.5 h-3.5" aria-hidden="true" />
                          ) : isSorted === 'desc' ? (
                            <ArrowDown className="w-3.5 h-3.5" aria-hidden="true" />
                          ) : (
                            <ArrowUpDown className="w-3 h-3 opacity-40" aria-hidden="true" />
                          )}
                        </span>
                      )}
                    </div>
                  </th>
                );
              })}
            </tr>
          ))}
        </thead>

        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={columns.length} className="text-center py-8 text-wp-text-muted">
                {isLoading ? t('datagrid.loading') : t('datagrid.empty')}
              </td>
            </tr>
          ) : (
            <>
              {virtualItems.length > 0 && virtualItems[0] && (
                <tr style={{ height: `${virtualItems[0].start}px` }}>
                  <td colSpan={columns.length} />
                </tr>
              )}

              {(virtualItems.length > 0
                ? virtualItems.map((virtualRow) => ({ row: rows[virtualRow.index], index: virtualRow.index }))
                : rows.slice(0, 50).map((row, index) => ({ row, index }))
              ).map(({ row, index }) => {
                if (!row) return null;

                const isSelected = row.getIsSelected();
                const isFocused = index === focusedRowIndex;

                return (
                  <tr
                    key={row.id}
                    role="row"
                    data-index={index}
                    aria-selected={isSelected}
                    onClick={() => setFocusedRowIndex(index)}
                    className={cn(
                      'border-b border-wp-border/50 transition-colors',
                      isSelected && 'bg-wp-surface-raised font-medium',
                      isFocused && 'ring-1 ring-inset ring-wp-border',
                      !isSelected && 'hover:bg-wp-surface-raised/40',
                    )}
                    style={{ height: `${rowHeight}px` }}
                  >
                    {row.getVisibleCells().map((cell) => (
                      <td key={cell.id} className="py-1 px-3 truncate">
                        {flexRender(cell.column.columnDef.cell, cell.getContext())}
                      </td>
                    ))}
                  </tr>
                );
              })}

              {virtualItems.length > 0 && virtualItems[virtualItems.length - 1] && (
                <tr
                  style={{
                    height: `${totalVirtualHeight - (virtualItems[virtualItems.length - 1]?.end ?? 0)}px`,
                  }}
                >
                  <td colSpan={columns.length} />
                </tr>
              )}
            </>
          )}
        </tbody>
      </table>

      {hasNextPage && onFetchNextPage && (
        <div className="p-2 border-t border-wp-border bg-wp-surface flex justify-center">
          <button
            type="button"
            onClick={() => onFetchNextPage()}
            className="px-3 py-1 text-xs rounded border border-wp-border hover:bg-wp-surface-raised cursor-pointer text-wp-text"
          >
            {t('datagrid.loadMore')}
          </button>
        </div>
      )}

      {isLoading && (
        <div className="flex items-center justify-center py-2 text-xs text-wp-text-muted gap-1 border-t border-wp-border bg-wp-surface">
          <RefreshCw className="w-3.5 h-3.5 animate-spin" aria-hidden="true" />
          <span>{t('datagrid.loading')}</span>
        </div>
      )}
    </div>
  );
}
