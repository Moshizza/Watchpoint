import { formatDuration, useCulture, useT } from '@watchpoint/i18n';
import { cn } from '../utils.ts';

export interface DurationInputProps {
  value: string;
  onChange: (iso: string) => void;
  className?: string;
}

function parseIso(iso: string) {
  const regex = /P(?:([0-9]+)D)?(?:T(?:([0-9]+)H)?(?:([0-9]+)M)?(?:([0-9]+)S)?)?/;
  const matches = regex.exec(iso || 'PT0S') ?? [];
  return {
    days: parseInt(matches[1] ?? '0', 10),
    hours: parseInt(matches[2] ?? '0', 10),
    minutes: parseInt(matches[3] ?? '0', 10),
    seconds: parseInt(matches[4] ?? '0', 10),
  };
}

export function DurationInput({ value, onChange, className }: DurationInputProps) {
  const { culture } = useCulture();
  const t = useT('app');

  const { days, hours, minutes, seconds } = parseIso(value);

  const update = (d: number, h: number, min: number, s: number) => {
    let iso = 'P';
    if (d > 0) iso += `${d}D`;
    if (h > 0 || min > 0 || s > 0 || d === 0) {
      iso += 'T';
      if (h > 0) iso += `${h}H`;
      if (min > 0) iso += `${min}M`;
      if (s > 0 || (h === 0 && min === 0 && d === 0)) iso += `${s}S`;
    }
    onChange(iso);
  };

  return (
    <div data-testid="duration-input" className={cn('flex flex-col gap-1', className)}>
      <div className="flex items-center gap-2">
        <div className="flex items-center gap-1">
          <input
            type="number"
            min="0"
            data-testid="duration-hours"
            value={hours}
            onChange={(e) => {
              const h = Math.max(0, parseInt(e.target.value || '0', 10));
              update(days, h, minutes, seconds);
            }}
            className="w-16 px-2 py-1 rounded bg-wp-surface border border-wp-border text-sm text-wp-text font-mono"
          />
          <span className="text-xs text-wp-text-muted">{t('form.hoursUnit')}</span>
        </div>

        <div className="flex items-center gap-1">
          <input
            type="number"
            min="0"
            max="59"
            data-testid="duration-minutes"
            value={minutes}
            onChange={(e) => {
              const min = Math.max(0, parseInt(e.target.value || '0', 10));
              update(days, hours, min, seconds);
            }}
            className="w-16 px-2 py-1 rounded bg-wp-surface border border-wp-border text-sm text-wp-text font-mono"
          />
          <span className="text-xs text-wp-text-muted">{t('form.minutesUnit')}</span>
        </div>

        <div className="flex items-center gap-1">
          <input
            type="number"
            min="0"
            max="59"
            data-testid="duration-seconds"
            value={seconds}
            onChange={(e) => {
              const s = Math.max(0, parseInt(e.target.value || '0', 10));
              update(days, hours, minutes, s);
            }}
            className="w-16 px-2 py-1 rounded bg-wp-surface border border-wp-border text-sm text-wp-text font-mono"
          />
          <span className="text-xs text-wp-text-muted">{t('form.secondsUnit')}</span>
        </div>
      </div>

      <span data-testid="duration-formatted" className="text-xs text-wp-text-muted">
        {formatDuration(value, culture)}
      </span>
    </div>
  );
}
