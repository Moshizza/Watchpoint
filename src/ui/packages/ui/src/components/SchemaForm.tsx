import { useT } from '@watchpoint/i18n';
import { Eye, EyeOff, Lock } from 'lucide-react';
import { useState } from 'react';
import { cn } from '../utils.ts';

export interface JSONSchemaProperty {
  type?: 'string' | 'number' | 'integer' | 'boolean' | 'array' | 'object';
  title?: string;
  description?: string;
  default?: unknown;
  enum?: (string | number)[];
  minimum?: number;
  maximum?: number;
  'x-wp-ui'?: {
    group?: string;
    advanced?: boolean;
    secret?: boolean;
    visibleWhen?: {
      field: string;
      equals: unknown;
    };
  };
}

export interface JSONSchema {
  type: 'object';
  properties: Record<string, JSONSchemaProperty>;
  required?: string[];
}

export interface SchemaFormProps {
  schema: JSONSchema;
  values: Record<string, unknown>;
  onChange: (values: Record<string, unknown>) => void;
  fieldErrors?: Record<string, string>;
  className?: string;
}

export function SchemaForm({ schema, values, onChange, fieldErrors, className }: SchemaFormProps) {
  const t = useT('app');
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [unmaskedSecrets, setUnmaskedSecrets] = useState<Set<string>>(new Set());
  const [replacedSecrets, setReplacedSecrets] = useState<Set<string>>(new Set());

  const handleFieldChange = (key: string, val: unknown) => {
    onChange({
      ...values,
      [key]: val,
    });
  };

  const isVisible = (prop: JSONSchemaProperty): boolean => {
    const visibleWhen = prop['x-wp-ui']?.visibleWhen;
    if (!visibleWhen) return true;
    return values[visibleWhen.field] === visibleWhen.equals;
  };

  const regularFields: [string, JSONSchemaProperty][] = [];
  const advancedFields: [string, JSONSchemaProperty][] = [];

  for (const [key, prop] of Object.entries(schema.properties)) {
    if (!isVisible(prop)) continue;
    if (prop['x-wp-ui']?.advanced) {
      advancedFields.push([key, prop]);
    } else {
      regularFields.push([key, prop]);
    }
  }

  const renderField = (key: string, prop: JSONSchemaProperty) => {
    const isSecret = prop['x-wp-ui']?.secret;
    const pointer = `/${key}`;
    const altPointer = `/settings/${key}`;
    const errorMsg = fieldErrors?.[pointer] ?? fieldErrors?.[altPointer];
    const isRequired = schema.required?.includes(key);
    const val = values[key] ?? prop.default ?? '';

    return (
      <div key={key} data-testid={`schema-field-${key}`} className="flex flex-col gap-1.5">
        <label htmlFor={`field-${key}`} className="text-sm font-medium text-wp-text flex items-center justify-between">
          <span>
            {prop.title ?? key} {isRequired && <span className="text-wp-state-alarm">*</span>}
          </span>
          {isSecret && (
            <span className="text-xs text-wp-text-muted flex items-center gap-1">
              <Lock className="w-3 h-3" aria-hidden="true" />
            </span>
          )}
        </label>

        {isSecret ? (
          <div className="flex flex-col gap-1.5">
            {val && !replacedSecrets.has(key) ? (
              <div className="flex items-center gap-2">
                <span className="px-3 py-1.5 rounded bg-wp-surface-raised border border-wp-border text-sm font-mono text-wp-text-muted select-none">
                  {t('form.secretSet')}
                </span>
                <button
                  type="button"
                  onClick={() => {
                    setReplacedSecrets((prev) => new Set(prev).add(key));
                    handleFieldChange(key, '');
                  }}
                  className="px-2 py-1 text-xs rounded border border-wp-border hover:bg-wp-surface-raised text-wp-text cursor-pointer"
                >
                  {t('form.secretReplace')}
                </button>
              </div>
            ) : (
              <div className="relative flex items-center">
                <input
                  id={`field-${key}`}
                  type={unmaskedSecrets.has(key) ? 'text' : 'password'}
                  value={String(val)}
                  onChange={(e) => handleFieldChange(key, e.target.value)}
                  className="w-full px-3 py-1.5 rounded bg-wp-surface border border-wp-border text-sm text-wp-text focus:outline-hidden focus:ring-1 focus:ring-wp-border pe-9 font-mono"
                />
                <button
                  type="button"
                  tabIndex={-1}
                  aria-label={unmaskedSecrets.has(key) ? t('dialog.close') : t('dialog.confirm')}
                  onClick={() => {
                    setUnmaskedSecrets((prev) => {
                      const next = new Set(prev);
                      if (next.has(key)) next.delete(key);
                      else next.add(key);
                      return next;
                    });
                  }}
                  className="absolute end-2 text-wp-text-muted hover:text-wp-text cursor-pointer p-0.5"
                >
                  {unmaskedSecrets.has(key) ? (
                    <EyeOff className="w-4 h-4" aria-hidden="true" />
                  ) : (
                    <Eye className="w-4 h-4" aria-hidden="true" />
                  )}
                </button>
              </div>
            )}
          </div>
        ) : prop.type === 'boolean' ? (
          <div className="flex items-center gap-2">
            <input
              id={`field-${key}`}
              type="checkbox"
              checked={Boolean(val)}
              onChange={(e) => handleFieldChange(key, e.target.checked)}
              className="rounded border-wp-border text-wp-state-normal focus:ring-wp-border"
            />
            {prop.description && <span className="text-xs text-wp-text-muted">{prop.description}</span>}
          </div>
        ) : prop.enum ? (
          <select
            id={`field-${key}`}
            value={String(val)}
            onChange={(e) => handleFieldChange(key, e.target.value)}
            className="w-full px-3 py-1.5 rounded bg-wp-surface border border-wp-border text-sm text-wp-text focus:outline-hidden focus:ring-1 focus:ring-wp-border"
          >
            {prop.enum.map((opt) => (
              <option key={String(opt)} value={String(opt)}>
                {String(opt)}
              </option>
            ))}
          </select>
        ) : (
          <input
            id={`field-${key}`}
            type={prop.type === 'number' || prop.type === 'integer' ? 'number' : 'text'}
            value={String(val)}
            onChange={(e) => {
              const v = e.target.value;
              handleFieldChange(
                key,
                prop.type === 'number' || prop.type === 'integer' ? (v === '' ? '' : Number(v)) : v,
              );
            }}
            className="w-full px-3 py-1.5 rounded bg-wp-surface border border-wp-border text-sm text-wp-text focus:outline-hidden focus:ring-1 focus:ring-wp-border"
          />
        )}

        {prop.description && !prop.type?.includes('boolean') && (
          <span className="text-xs text-wp-text-muted">{prop.description}</span>
        )}

        {errorMsg && (
          <span data-testid={`field-error-${key}`} className="text-xs text-wp-state-alarm font-medium">
            {errorMsg}
          </span>
        )}
      </div>
    );
  };

  return (
    <form
      data-testid="schema-form"
      onSubmit={(e) => e.preventDefault()}
      className={cn('space-y-4 p-4 border border-wp-border rounded bg-wp-surface', className)}
    >
      <div className="space-y-4">{regularFields.map(([key, prop]) => renderField(key, prop))}</div>

      {advancedFields.length > 0 && (
        <div className="pt-3 border-t border-wp-border">
          <button
            type="button"
            data-testid="toggle-advanced"
            onClick={() => setShowAdvanced(!showAdvanced)}
            className="text-xs font-medium text-wp-text-muted hover:text-wp-text cursor-pointer flex items-center gap-1"
          >
            <span>{t('form.advanced')}</span>
          </button>
          {showAdvanced && (
            <div data-testid="advanced-fields" className="mt-4 space-y-4 ps-2 border-s border-wp-border">
              {advancedFields.map(([key, prop]) => renderField(key, prop))}
            </div>
          )}
        </div>
      )}
    </form>
  );
}
