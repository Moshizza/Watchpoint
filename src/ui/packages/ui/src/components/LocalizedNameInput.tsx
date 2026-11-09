import { useT } from '@watchpoint/i18n';
import { Globe } from 'lucide-react';
import { useState } from 'react';
import { cn } from '../utils.ts';

export interface LocalizedNameInputProps {
  value: {
    default: string;
    translations?: Record<string, string>;
  };
  onChange: (value: { default: string; translations?: Record<string, string> }) => void;
  className?: string;
}

export function LocalizedNameInput({ value, onChange, className }: LocalizedNameInputProps) {
  const t = useT('app');
  const [selectedLang, setSelectedLang] = useState<string>('fr');

  const supportedLanguages = ['fr', 'ar', 'de', 'es'];

  const handleDefaultChange = (newDefault: string) => {
    const updated: { default: string; translations?: Record<string, string> } = {
      default: newDefault,
    };
    if (value.translations) {
      updated.translations = value.translations;
    }
    onChange(updated);
  };

  const handleTranslationChange = (lang: string, trans: string) => {
    const nextTrans: Record<string, string> = {};
    for (const [k, v] of Object.entries(value.translations ?? {})) {
      if (k !== lang && v.trim() !== '') {
        nextTrans[k] = v;
      }
    }
    if (trans.trim() !== '') {
      nextTrans[lang] = trans;
    }

    const updated: { default: string; translations?: Record<string, string> } = {
      default: value.default,
    };
    if (Object.keys(nextTrans).length > 0) {
      updated.translations = nextTrans;
    }
    onChange(updated);
  };

  return (
    <div data-testid="localized-name-input" className={cn('space-y-2', className)}>
      <div className="flex flex-col gap-1">
        <label htmlFor="default-name-input" className="text-xs font-medium text-wp-text-muted">
          {t('form.defaultLanguage')}
        </label>
        <input
          id="default-name-input"
          type="text"
          data-testid="localized-default-input"
          value={value.default}
          onChange={(e) => handleDefaultChange(e.target.value)}
          className="w-full px-3 py-1.5 rounded bg-wp-surface border border-wp-border text-sm text-wp-text focus:outline-hidden focus:ring-1 focus:ring-wp-border"
        />
      </div>

      <div className="flex flex-col gap-1.5 pt-1 border-t border-wp-border">
        <div className="flex items-center justify-between text-xs text-wp-text-muted">
          <span className="flex items-center gap-1">
            <Globe className="w-3 h-3" aria-hidden="true" />
            <span>{t('form.translations')}</span>
          </span>
          <div className="flex gap-1">
            {supportedLanguages.map((lang) => (
              <button
                key={lang}
                type="button"
                onClick={() => setSelectedLang(lang)}
                className={cn(
                  'px-1.5 py-0.5 rounded text-xs cursor-pointer font-mono',
                  selectedLang === lang
                    ? 'bg-wp-surface-raised font-bold text-wp-text'
                    : 'hover:bg-wp-surface-raised/40',
                )}
              >
                {lang}
              </button>
            ))}
          </div>
        </div>

        <input
          type="text"
          data-testid={`localized-trans-input-${selectedLang}`}
          placeholder={t('form.translations')}
          value={value.translations?.[selectedLang] ?? ''}
          onChange={(e) => handleTranslationChange(selectedLang, e.target.value)}
          className="w-full px-3 py-1.5 rounded bg-wp-surface border border-wp-border text-sm text-wp-text focus:outline-hidden focus:ring-1 focus:ring-wp-border"
        />
      </div>
    </div>
  );
}
