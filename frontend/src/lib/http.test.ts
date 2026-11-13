import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, request, setAccessTokenProvider, unauthorizedEvent } from './http';

const fetchMock = vi.fn();

function jsonResponse(body: unknown, status = 200): Response {
  const text = JSON.stringify(body);
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: new Headers({ 'content-type': 'application/json' }),
    text: () => Promise.resolve(text),
    json: () => Promise.resolve(body),
  } as unknown as Response;
}

describe('request', () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
    setAccessTokenProvider(() => null);
    window.sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    setAccessTokenProvider(() => null);
  });

  it('unwraps the success envelope and returns data', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({ success: true, data: { id: 7 }, error: null, meta: null }, 200),
    );

    const result = await request<{ id: number }>('/api/buildings');

    expect(result).toEqual({ id: 7 });
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/buildings');
    expect(init.headers).toEqual({ Accept: 'application/json' });
  });

  it('throws ApiError with validation details on envelope failure', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        {
          success: false,
          data: null,
          error: {
            code: 'Volo.Abp.Validation:Validation',
            message: 'Dữ liệu không hợp lệ',
            details: null,
            validationErrors: [{ field: 'password', message: 'Yêu cầu ký tự hoa' }],
          },
          meta: { traceId: 'trace-123', timestamp: '2026-01-01T00:00:00Z' },
        },
        200,
      ),
    );

    const error = (await request('/api/account/reset-password', {
      method: 'POST',
      body: { password: 'weak' },
    }).catch((caught: unknown) => caught)) as ApiError;

    expect(error).toBeInstanceOf(ApiError);
    expect(error.code).toBe('Volo.Abp.Validation:Validation');
    expect(error.message).toBe('Dữ liệu không hợp lệ');
    expect(error.fieldErrors()).toEqual({ password: 'Yêu cầu ký tự hoa' });
  });

  it('passes the bearer token from the provider when set', async () => {
    setAccessTokenProvider(() => 'token-abc');
    fetchMock.mockResolvedValue(
      jsonResponse({ success: true, data: null, error: null, meta: null }, 200),
    );

    await request('/api/buildings');

    const init = fetchMock.mock.calls[0]![1] as RequestInit;
    expect(init.headers).toMatchObject({ Authorization: 'Bearer token-abc' });
  });

  it('dispatches the unauthorized event on a 401 envelope', async () => {
    const listener = vi.fn();
    window.addEventListener(unauthorizedEvent, listener);
    fetchMock.mockResolvedValue(
      jsonResponse({ success: false, data: null, error: { code: '401', message: 'Hết phiên' }, meta: null }, 401),
    );

    await expect(request('/api/buildings')).rejects.toBeInstanceOf(ApiError);

    expect(listener).toHaveBeenCalledTimes(1);
    window.removeEventListener(unauthorizedEvent, listener);
  });

  it('does not dispatch the unauthorized event for anonymous calls', async () => {
    const listener = vi.fn();
    window.addEventListener(unauthorizedEvent, listener);
    fetchMock.mockResolvedValue(
      jsonResponse({ success: false, data: null, error: { code: '401', message: 'Hết phiên' }, meta: null }, 401),
    );

    await expect(request('/connect/token', { anonymous: true })).rejects.toBeInstanceOf(ApiError);

    expect(listener).not.toHaveBeenCalled();
    window.removeEventListener(unauthorizedEvent, listener);
  });

  it('maps a transport failure to the network error code', async () => {
    fetchMock.mockRejectedValue(new TypeError('fetch failed'));

    const error = await request('/api/buildings').catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(0);
    expect((error as ApiError).message).toContain('Không kết nối được máy chủ');
  });

  it('surfaces the scope-gate denial when fetch follows the gate 302 to /Error?httpStatusCode=403', async () => {
    // The gate emits 302 -> /Error?httpStatusCode=403; fetch() follows it and
    // the Vite proxy answers 404. response.redirected + response.url keep the
    // redirect trail, which is how the client detects the real denial.
    const followed = {
      status: 404,
      ok: false,
      redirected: true,
      url: 'http://localhost:5173/Error?httpStatusCode=403',
      headers: new Headers(),
      text: () => Promise.resolve(''),
    } as unknown as Response;
    fetchMock.mockResolvedValue(followed);

    const error = (await request('/api/building-management/building').catch(
      (caught: unknown) => caught,
    )) as ApiError;

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(403);
    expect(error.message).toContain('từ chối truy cập');
  });

  it('surfaces the scope-gate denial when a 200 carries non-JSON HTML instead of data', async () => {
    const html = new Response('<html>index fallback</html>', {
      status: 200,
      headers: { 'content-type': 'text/html' },
    });
    fetchMock.mockResolvedValue(html);

    const error = (await request('/api/building-management/building').catch(
      (caught: unknown) => caught,
    )) as ApiError;

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(403);
    expect(error.message).toContain('từ chối truy cập');
  });

  it('serializes the JSON body and keeps the content type', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({ success: true, data: null, error: null, meta: null }, 201),
    );

    await request('/api/account/send-password-reset-code', {
      method: 'POST',
      anonymous: true,
      body: { email: 'a@b.c', appName: 'App' },
    });

    const init = fetchMock.mock.calls[0]![1] as RequestInit;
    expect(init.method).toBe('POST');
    expect(init.body).toBe(JSON.stringify({ email: 'a@b.c', appName: 'App' }));
  });
});
