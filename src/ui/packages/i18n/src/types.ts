export interface ApiError {
  status: number;
  code: string;
  messageKey: string;
  args?: Record<string, unknown>;
  fieldErrors?: Record<string, string>;
  traceId?: string;
  message?: string;
}

export interface NetworkError {
  code: 'common.network_error';
  message: string;
  traceId?: string;
}
