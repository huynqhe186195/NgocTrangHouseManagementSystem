import { beforeEach, describe, expect, it, vi } from 'vitest';
import { authClient } from './authClient';
import { ApiError } from '../http';

const fetchMock = vi.fn();

function tokenResponse(body: Record<string, unknown>, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(JSON.stringify(body)),
  } as unknown as Response;
}

describe('authClient.login', () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  it('posts the ROPC password grant and maps the token set', async () => {
    fetchMock.mockResolvedValue(
      tokenResponse({ access_token: 'at', refresh_token: 'rt', expires_in: 3600, token_type: 'Bearer' }),
    );

    const tokens = await authClient.login('admin', 'secret');

    expect(tokens.accessToken).toBe('at');
    expect(tokens.refreshToken).toBe('rt');
    expect(tokens.expiresAt).toBeGreaterThan(Date.now());

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/connect/token');
    expect(init.method).toBe('POST');
    expect(init.credentials).toBe('omit');
    const body = init.body as string;
    expect(body).toContain('grant_type=password');
    expect(body).toContain('client_id=NgocTrangHouseManagementSystem_App');
    expect(body).toContain('scope=openid+profile+email+roles+NgocTrangHouseManagementSystem');
  });

  it('maps invalid_grant to a friendly message', async () => {
    fetchMock.mockResolvedValue(tokenResponse({ error: 'invalid_grant' }, 400));

    const error = await authClient.login('admin', 'wrong').catch((c: unknown) => c);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).message).toBe('Email/số điện thoại hoặc mật khẩu không đúng.');
  });

  it('maps unsupported_grant_type when the server forbids ROPC', async () => {
    fetchMock.mockResolvedValue(tokenResponse({ error: 'unsupported_grant_type' }, 400));

    const error = await authClient.login('admin', 'secret').catch((c: unknown) => c);

    expect((error as ApiError).message).toBe(
      'Máy chủ không cho phép đăng nhập bằng mật khẩu.',
    );
  });

  it('maps a transport failure to the network error code', async () => {
    fetchMock.mockRejectedValue(new TypeError('fetch failed'));

    const error = await authClient.login('admin', 'secret').catch((c: unknown) => c);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(0);
  });

  it('treats a non-token payload as invalid even with a 200', async () => {
    fetchMock.mockResolvedValue(tokenResponse({ unrelated: true }));

    await expect(authClient.login('admin', 'secret')).rejects.toThrow('Máy chủ trả về dữ liệu không hợp lệ.');
  });
});

describe('authClient.refresh', () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  it('posts the refresh grant and maps the new token set', async () => {
    fetchMock.mockResolvedValue(
      tokenResponse({ access_token: 'at2', expires_in: 1800, token_type: 'Bearer' }),
    );

    const tokens = await authClient.refresh('rt');

    expect(tokens.accessToken).toBe('at2');
    expect(tokens.refreshToken).toBeNull();
    const body = fetchMock.mock.calls[0]![1].body as string;
    expect(body).toContain('grant_type=refresh_token');
    expect(body).toContain('refresh_token=rt');
  });
});
