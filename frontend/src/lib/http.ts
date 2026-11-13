import { apiBase } from './config';

/** Shapes produced by NgocTrangHouseManagementSystem.HttpApi/Responses. */
export interface ApiValidationError {
  field: string | null;
  message: string;
}

export interface ApiErrorBody {
  code: string;
  message: string;
  details: string | null;
  validationErrors: ApiValidationError[];
}

interface Envelope<T> {
  success: boolean;
  data: T | null;
  error: ApiErrorBody | null;
  meta: { traceId: string; timestamp: string };
}

export class ApiError extends Error {
  readonly code: string;
  readonly validationErrors: ApiValidationError[];
  readonly status: number;
  readonly traceId: string | null;

  constructor(
    code: string,
    message: string,
    status: number,
    validationErrors: ApiValidationError[] = [],
    traceId: string | null = null,
  ) {
    super(message);
    this.name = 'ApiError';
    this.code = code;
    this.status = status;
    this.validationErrors = validationErrors;
    this.traceId = traceId;
  }

  /** Per-field messages, keyed by the DTO property name the server reported. */
  fieldErrors(): Record<string, string> {
    const map: Record<string, string> = {};
    for (const item of this.validationErrors) {
      if (item.field) {
        map[item.field] = item.message;
      }
    }
    return map;
  }
}

/** Fired when the API rejects the current token so the shell can sign out. */
export const unauthorizedEvent = 'ngoctrang:unauthorized';

let accessTokenProvider: () => string | null = () => null;

export function setAccessTokenProvider(provider: () => string | null): void {
  accessTokenProvider = provider;
}

function isEnvelope(value: unknown): value is Envelope<unknown> {
  return (
    typeof value === 'object' &&
    value !== null &&
    'success' in value &&
    'meta' in value
  );
}

/**
 * Paths the backend leaves unwrapped (ApiResponseFilter.ShouldSkipWrapping).
 * Everything else, including /api/account/*, comes back inside the envelope.
 */
function isRawPath(path: string): boolean {
  return (
    path.startsWith('/connect/') ||
    path.startsWith('/api/abp/') ||
    path.startsWith('/.well-known/')
  );
}

const TRANSPORT_MESSAGE =
  'Không kết nối được máy chủ. Kiểm tra API đang chạy và chứng chỉ HTTPS.';

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
  /** Skip the Authorization header (login, password reset). */
  anonymous?: boolean;
  /** Override the 401 side effect, e.g. while probing a session on boot. */
  suppressUnauthorizedEvent?: boolean;
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, anonymous = false, suppressUnauthorizedEvent = false } = options;

  const headers: Record<string, string> = { Accept: 'application/json' };
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  const token = anonymous ? null : accessTokenProvider();
  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  let response: Response;
  try {
    response = await fetch(`${apiBase}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      credentials: 'omit',
    });
  } catch {
    throw new ApiError('Common:NetworkError', TRANSPORT_MESSAGE, 0);
  }

  const text = await response.text();
  let parsed: unknown = null;
  if (text.length > 0) {
    try {
      parsed = JSON.parse(text);
    } catch {
      parsed = null;
    }
  }

  if (response.status === 401 && !suppressUnauthorizedEvent && !anonymous) {
    window.dispatchEvent(new CustomEvent(unauthorizedEvent));
  }

  if (isRawPath(path)) {
    if (!response.ok) {
      throw new ApiError(
        `HTTP:${response.status}`,
        extractRawMessage(parsed) ?? `Yêu cầu thất bại (${response.status}).`,
        response.status,
      );
    }
    return parsed as T;
  }

  if (isEnvelope(parsed)) {
    if (!parsed.success) {
      const error = parsed.error;
      throw new ApiError(
        error?.code ?? 'Common:Unknown',
        error?.message ?? 'Đã xảy ra lỗi.',
        response.status,
        error?.validationErrors ?? [],
        parsed.meta?.traceId ?? null,
      );
    }
    return parsed.data as T;
  }

  // Any other unwrapped payload (e.g. a proxy or HTML error page).
  if (!response.ok) {
    throw new ApiError(
      `HTTP:${response.status}`,
      `Yêu cầu thất bại (${response.status}).`,
      response.status,
    );
  }
  return parsed as T;
}

function extractRawMessage(payload: unknown): string | null {
  if (typeof payload !== 'object' || payload === null) {
    return null;
  }
  const record = payload as Record<string, unknown>;
  for (const key of ['error_description', 'message', 'error']) {
    const value = record[key];
    if (typeof value === 'string' && value.length > 0) {
      return value;
    }
  }
  return null;
}
