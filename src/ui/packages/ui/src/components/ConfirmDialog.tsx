import * as Dialog from '@radix-ui/react-dialog';
import { useCulture, useT } from '@watchpoint/i18n';
import { AlertTriangle } from 'lucide-react';
import type { ReactNode } from 'react';
import { cn } from '../utils.ts';

export interface ConfirmDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onConfirm: () => void;
  title: string;
  description: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  isDestructive?: boolean;
}

export function ConfirmDialog({
  open,
  onOpenChange,
  onConfirm,
  title,
  description,
  confirmLabel,
  cancelLabel,
  isDestructive = false,
}: ConfirmDialogProps) {
  const { dir } = useCulture();
  const t = useT('app');

  const cLabel = confirmLabel ?? t('dialog.confirm');
  const canLabel = cancelLabel ?? t('dialog.cancel');

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/60 backdrop-blur-xs z-50 animate-in fade-in" />
        <Dialog.Content
          dir={dir}
          data-testid="confirm-dialog"
          className={cn(
            'fixed top-1/2 start-1/2 -translate-x-1/2 -translate-y-1/2 z-50',
            'w-full max-w-md p-6 rounded-lg bg-wp-surface border border-wp-border shadow-xl',
            'flex flex-col gap-4 text-wp-text focus:outline-hidden',
          )}
        >
          <div className="flex items-start gap-3">
            {isDestructive && (
              <div className="p-2 rounded-full bg-wp-state-alarm-bg text-wp-state-alarm-fg shrink-0">
                <AlertTriangle className="w-5 h-5" aria-hidden="true" />
              </div>
            )}
            <div className="flex-1">
              <Dialog.Title className="text-lg font-semibold leading-none mb-2">{title}</Dialog.Title>
              <Dialog.Description className="text-sm text-wp-text-muted">{description}</Dialog.Description>
            </div>
          </div>

          <div className="flex justify-end gap-2 pt-2">
            <button
              type="button"
              data-testid="dialog-cancel"
              onClick={() => onOpenChange(false)}
              className="px-4 py-2 rounded text-sm font-medium border border-wp-border bg-wp-surface hover:bg-wp-surface-raised cursor-pointer transition-colors"
            >
              {canLabel}
            </button>
            <button
              type="button"
              data-testid="dialog-confirm"
              onClick={() => {
                onConfirm();
                onOpenChange(false);
              }}
              className={cn(
                'px-4 py-2 rounded text-sm font-medium text-wp-text-inverse cursor-pointer transition-colors',
                isDestructive
                  ? 'bg-wp-state-alarm hover:opacity-90 text-white'
                  : 'bg-wp-state-normal hover:opacity-90 text-white',
              )}
            >
              {cLabel}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
