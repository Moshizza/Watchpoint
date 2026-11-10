import { useT } from '@watchpoint/i18n';
import { AlertCircle, Check, Copy } from 'lucide-react';
import { useState } from 'react';
import { cn } from '../utils.ts';

export interface ErrorViewProps {
  title?: string;
  message?: string;
  traceId?: string;
  fieldErrors?: Record<string, string>;
  className?: string;
}

export function ErrorView({ title, message, traceId, fieldErrors, className }: ErrorViewProps) {
  const t = useT('app');
  const [copied, setCopied] = useState(false);

  const displayTitle = title ?? t('error.generic');

  const copyTraceId = () => {
    if (traceId && typeof navigator !== 'undefined' && navigator.clipboard) {
      void navigator.clipboard.writeText(traceId);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }
  };

  return (
    <div
      role="alert"
      data-testid="error-view"
      className={cn(
        'p-4 rounded-lg border border-wp-state-alarm bg-wp-state-alarm-bg text-wp-state-alarm-fg flex flex-col gap-3',
        className,
      )}
    >
      <div className="flex items-start gap-3">
        <AlertCircle className="w-5 h-5 shrink-0 mt-0.5 text-wp-state-alarm" aria-hidden="true" />
        <div className="flex-1">
          <h3 className="font-semibold text-base leading-tight">{displayTitle}</h3>
          {message && <p className="mt-1 text-sm opacity-90">{message}</p>}

          {fieldErrors && Object.keys(fieldErrors).length > 0 && (
            <ul className="mt-2 text-xs space-y-1 list-disc ps-4">
              {Object.entries(fieldErrors).map(([field, err]) => (
                <li key={field}>
                  <strong>{field}:</strong> {err}
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>

      {traceId && (
        <div className="flex items-center justify-between text-xs pt-2 border-t border-wp-state-alarm/20">
          <span className="font-mono opacity-80 truncate">
            {t('error.traceId')}: {traceId}
          </span>
          <button
            type="button"
            onClick={copyTraceId}
            title={t('error.copyTraceId')}
            aria-label={t('error.copyTraceId')}
            className="inline-flex items-center gap-1 px-2 py-1 rounded bg-wp-surface text-wp-text hover:bg-wp-surface-raised cursor-pointer transition-colors"
          >
            {copied ? (
              <>
                <Check className="w-3 h-3 text-wp-state-normal" aria-hidden="true" />
                <span>{t('error.copied')}</span>
              </>
            ) : (
              <>
                <Copy className="w-3 h-3" aria-hidden="true" />
                <span>{t('error.copyTraceId')}</span>
              </>
            )}
          </button>
        </div>
      )}
    </div>
  );
}
