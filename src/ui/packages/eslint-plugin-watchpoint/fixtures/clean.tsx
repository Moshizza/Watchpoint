declare function t(key: string): string;

export const Row = () => (
  <div className="ms-2 text-end" title={t('row.title')}>
    {t('row.label')} —
  </div>
);
