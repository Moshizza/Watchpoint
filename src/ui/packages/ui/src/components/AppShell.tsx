import { useCulture, useT } from '@watchpoint/i18n';
import type { ReactNode } from 'react';
import { cn } from '../utils.ts';

export interface AppShellProps {
  header?: ReactNode;
  navigator?: ReactNode;
  children: ReactNode;
  contextPanel?: ReactNode;
  bottomPanel?: ReactNode;
  className?: string;
}

export function AppShell({ header, navigator, children, contextPanel, bottomPanel, className }: AppShellProps) {
  const { dir } = useCulture();
  const t = useT('app');

  return (
    <div
      dir={dir}
      data-testid="app-shell"
      className={cn('flex flex-col h-screen w-screen overflow-hidden bg-wp-bg text-wp-text', className)}
    >
      {header && (
        <header
          data-testid="app-shell-header"
          className="h-14 border-b border-wp-border bg-wp-surface flex items-center px-4 shrink-0"
        >
          {header}
        </header>
      )}

      <div className="flex flex-1 min-h-0 overflow-hidden">
        {navigator && (
          <aside
            data-testid="app-shell-navigator"
            aria-label={t('nav.devices')}
            className="w-64 border-e border-wp-border bg-wp-surface flex flex-col shrink-0 overflow-y-auto"
          >
            {navigator}
          </aside>
        )}

        <main data-testid="app-shell-main" className="flex-1 flex flex-col min-w-0 overflow-y-auto bg-wp-bg">
          <div className="flex-1 p-4">{children}</div>
          {bottomPanel && (
            <div
              data-testid="app-shell-bottom-panel"
              className="border-t border-wp-border bg-wp-surface min-h-[160px] p-2 shrink-0"
            >
              {bottomPanel}
            </div>
          )}
        </main>

        {contextPanel && (
          <aside
            data-testid="app-shell-context-panel"
            aria-label={t('nav.settings')}
            className="w-80 border-s border-wp-border bg-wp-surface flex flex-col shrink-0 overflow-y-auto"
          >
            {contextPanel}
          </aside>
        )}
      </div>
    </div>
  );
}
