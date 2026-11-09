import { formatDateTime, useCulture } from '@watchpoint/i18n';
import { Calendar } from 'lucide-react';
import { cn } from '../utils.ts';

export interface DateTimeInputProps {
  value: Date | string;
  onChange: (d: Date) => void;
  timeZone?: string;
  className?: string;
}

function toLocalInputString(date: Date, timeZone: string): string {
  const formatter = new Intl.DateTimeFormat('en-US', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  });

  const parts = formatter.formatToParts(date);
  let y = '',
    m = '',
    d = '',
    h = '',
    min = '';
  for (const p of parts) {
    if (p.type === 'year') y = p.value;
    if (p.type === 'month') m = p.value;
    if (p.type === 'day') d = p.value;
    if (p.type === 'hour') h = p.value === '24' ? '00' : p.value;
    if (p.type === 'minute') min = p.value;
  }
  return `${y}-${m}-${d}T${h}:${min}`;
}

function parseLocalInputString(input: string, timeZone: string): Date {
  const [datePart, timePart] = input.split('T');
  if (!datePart || !timePart) return new Date();

  const [yearStr, monthStr, dayStr] = datePart.split('-');
  const [hourStr, minStr] = timePart.split(':');

  const year = parseInt(yearStr ?? '1970', 10);
  const month = parseInt(monthStr ?? '1', 10);
  const day = parseInt(dayStr ?? '1', 10);
  const hour = parseInt(hourStr ?? '0', 10);
  const minute = parseInt(minStr ?? '0', 10);

  let utcGuess = new Date(Date.UTC(year, month - 1, day, hour, minute, 0));
  for (let i = 0; i < 3; i++) {
    const formatted = toLocalInputString(utcGuess, timeZone);
    const [fDate, fTime] = formatted.split('T');
    const [fy, fm, fd] = (fDate ?? '').split('-').map(Number);
    const [fh, fmin] = (fTime ?? '').split(':').map(Number);

    const diffMinutes =
      (year - (fy ?? 0)) * 525600 +
      (month - (fm ?? 0)) * 43200 +
      (day - (fd ?? 0)) * 1440 +
      (hour - (fh ?? 0)) * 60 +
      (minute - (fmin ?? 0));

    if (diffMinutes === 0) break;
    utcGuess = new Date(utcGuess.getTime() + diffMinutes * 60000);
  }

  return utcGuess;
}

export function DateTimeInput({ value, onChange, timeZone = 'UTC', className }: DateTimeInputProps) {
  const { culture } = useCulture();
  const dateObj = typeof value === 'string' ? new Date(value) : value;

  const localString = toLocalInputString(dateObj, timeZone);

  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const newStr = e.target.value;
    if (newStr) {
      const parsed = parseLocalInputString(newStr, timeZone);
      onChange(parsed);
    }
  };

  return (
    <div data-testid="datetime-input" className={cn('flex flex-col gap-1', className)}>
      <div className="relative flex items-center">
        <input
          type="datetime-local"
          value={localString}
          onChange={handleChange}
          data-testid="datetime-native-input"
          className="w-full px-3 py-1.5 rounded bg-wp-surface border border-wp-border text-sm text-wp-text focus:outline-hidden focus:ring-1 focus:ring-wp-border font-mono"
        />
        <Calendar className="w-4 h-4 text-wp-text-muted absolute end-3 pointer-events-none" aria-hidden="true" />
      </div>
      <span data-testid="datetime-zone-label" className="text-xs text-wp-text-muted font-mono">
        {timeZone} (
        {formatDateTime(dateObj, {
          culture,
          timeZone,
          style: 'short',
        })}
        )
      </span>
    </div>
  );
}
