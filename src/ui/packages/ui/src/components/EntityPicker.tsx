import { useT } from '@watchpoint/i18n';
import { Check, ChevronDown, Search } from 'lucide-react';
import { useMemo, useState } from 'react';
import { cn } from '../utils.ts';

export interface EntityItem {
  id: string;
  name: string;
  kind: 'device' | 'server' | 'location' | 'user';
  tags?: string[];
  type?: string;
}

export interface EntityPickerProps {
  entities: EntityItem[];
  value?: string;
  onChange: (selectedId: string) => void;
  entityRefConstraints?: {
    kind?: 'device' | 'server' | 'location' | 'user';
    requiredTags?: string[];
    allowedTypes?: string[];
  };
  placeholder?: string;
  className?: string;
}

export function EntityPicker({
  entities,
  value,
  onChange,
  entityRefConstraints,
  placeholder,
  className,
}: EntityPickerProps) {
  const t = useT('app');
  const [query, setQuery] = useState('');
  const [isOpen, setIsOpen] = useState(false);

  const filteredEntities = useMemo(() => {
    return entities.filter((item) => {
      if (entityRefConstraints?.kind && item.kind !== entityRefConstraints.kind) {
        return false;
      }
      if (entityRefConstraints?.requiredTags && entityRefConstraints.requiredTags.length > 0) {
        const itemTags = new Set(item.tags ?? []);
        const hasAllTags = entityRefConstraints.requiredTags.every((t) => itemTags.has(t));
        if (!hasAllTags) return false;
      }
      if (entityRefConstraints?.allowedTypes && entityRefConstraints.allowedTypes.length > 0) {
        if (!item.type || !entityRefConstraints.allowedTypes.includes(item.type)) {
          return false;
        }
      }
      if (query.trim() !== '') {
        const q = query.toLowerCase();
        return item.name.toLowerCase().includes(q) || item.id.toLowerCase().includes(q);
      }
      return true;
    });
  }, [entities, entityRefConstraints, query]);

  const selectedEntity = entities.find((item) => item.id === value);
  const displayPlaceholder = placeholder ?? t('picker.select');

  return (
    <div data-testid="entity-picker" className={cn('relative w-full', className)}>
      <button
        type="button"
        data-testid="picker-trigger"
        onClick={() => setIsOpen(!isOpen)}
        className="w-full flex items-center justify-between px-3 py-2 rounded border border-wp-border bg-wp-surface text-sm text-wp-text hover:bg-wp-surface-raised cursor-pointer"
      >
        <span className={selectedEntity ? 'text-wp-text' : 'text-wp-text-muted truncate'}>
          {selectedEntity ? selectedEntity.name : displayPlaceholder}
        </span>
        <ChevronDown className="w-4 h-4 text-wp-text-muted shrink-0" aria-hidden="true" />
      </button>

      {isOpen && (
        <div
          data-testid="picker-popover"
          className="absolute z-20 top-full mt-1 w-full rounded border border-wp-border bg-wp-surface shadow-lg overflow-hidden"
        >
          <div className="p-2 border-b border-wp-border flex items-center gap-2">
            <Search className="w-4 h-4 text-wp-text-muted shrink-0" aria-hidden="true" />
            <input
              type="text"
              data-testid="picker-search"
              placeholder={t('picker.search')}
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              className="w-full bg-transparent text-sm text-wp-text focus:outline-hidden"
            />
          </div>

          <div role="listbox" className="max-h-56 overflow-y-auto p-1">
            {filteredEntities.length === 0 ? (
              <div className="p-3 text-center text-xs text-wp-text-muted">{t('picker.noResults')}</div>
            ) : (
              filteredEntities.map((item) => {
                const isSelected = item.id === value;
                return (
                  <button
                    type="button"
                    key={item.id}
                    role="option"
                    aria-selected={isSelected}
                    data-testid={`entity-option-${item.id}`}
                    onClick={() => {
                      onChange(item.id);
                      setIsOpen(false);
                      setQuery('');
                    }}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        onChange(item.id);
                        setIsOpen(false);
                        setQuery('');
                      }
                    }}
                    className={cn(
                      'w-full flex items-center justify-between px-2.5 py-1.5 rounded text-sm cursor-pointer transition-colors text-start',
                      isSelected ? 'bg-wp-surface-raised font-medium' : 'hover:bg-wp-surface-raised/50',
                    )}
                  >
                    <div className="flex flex-col">
                      <span className="text-wp-text">{item.name}</span>
                      <span className="text-xs text-wp-text-muted">{item.kind}</span>
                    </div>
                    {isSelected && <Check className="w-4 h-4 text-wp-state-normal shrink-0" aria-hidden="true" />}
                  </button>
                );
              })
            )}
          </div>
        </div>
      )}
    </div>
  );
}
