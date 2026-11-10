import { useCulture } from '@watchpoint/i18n';
import { toast, Toaster as SonnerToaster } from 'sonner';

export const Toaster = () => {
  const { dir } = useCulture();
  return <SonnerToaster dir={dir} richColors closeButton position="top-right" />;
};

export const ProblemToast = {
  error: (title: string, options?: { description?: string }) => {
    toast.error(title, options);
  },
  success: (title: string, options?: { description?: string }) => {
    toast.success(title, options);
  },
};
