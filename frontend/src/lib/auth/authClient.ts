import { apiBase, appConfig } from '../config';
import { ApiError } from '../http';
import type { TokenSet } from './tokenStore';

/**
 * OpenIddict token endpoint.
 *
 * The seeded NgocTrangHouseManagementSystem_App client is a public client
 * without a secret, and it enables the password grant, so the portal can
 * authenticate its own designed screens instead of redirecting to ABP's
 * Razor login page.
 *
 * NOTE: the password grant is a development/transitional flow. A production
 * portal should move to authorization-code + PKCE once a RootUrl is
 * configured for this client. That is deliberately out of scope here.
 */
interface TokenResponse {
  access_token: string;
  refresh_token?: string;
  expires_in: number;
  token_type: string;
}

function toTokenSet(body: TokenResponse): TokenSet {
  return {
    accessToken: body.access_token,
    refreshToken: body.refresh_token ?? null,
    expiresAt: Date.now() + body.expires_in * 1000,
  };
}

function describeTokenError(status: number, payload: unknown): ApiError {
  const record = (typeof payload === 'object' && payload !== null ? payload : {}) as Record<
    string,
    unknown
  >;
  const code = typeof record.error === 'string' ? record.error : `HTTP:${status}`;

  if (code === 'invalid_grant') {
    return new ApiError(code, 'Email/số điện thoại hoặc mật khẩu không đúng.', status);
  }
  if (code === 'invalid_client') {
    return new ApiError(code, 'Ứng dụng chưa được cấu hình trên máy chủ.', status);
  }
  if (code === 'unsupported_grant_type') {
    return new ApiError(code, 'Máy chủ không cho phép đăng nhập bằng mật khẩu.', status);
  }
  const description =
    typeof record.error_description === 'string' ? record.error_description : null;
  return new ApiError(code, description ?? 'Đăng nhập thất bại.', status);
}

async function postToken(form: URLSearchParams): Promise<TokenResponse> {
  let response: Response;
  try {
    response = await fetch(`${apiBase}/connect/token`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded',
        Accept: 'application/json',
      },
      body: form.toString(),
      credentials: 'omit',
    });
  } catch {
    throw new ApiError(
      'Common:NetworkError',
      'Không kết nối được máy chủ xác thực. Kiểm tra API đang chạy.',
      0,
    );
  }

  const text = await response.text();
  let payload: unknown = null;
  if (text.length > 0) {
    try {
      payload = JSON.parse(text);
    } catch {
      payload = null;
    }
  }

  if (!response.ok) {
    throw describeTokenError(response.status, payload);
  }
  if (typeof payload !== 'object' || payload === null || !('access_token' in payload)) {
    throw new ApiError('Common:InvalidTokenResponse', 'Máy chủ trả về dữ liệu không hợp lệ.', response.status);
  }
  return payload as TokenResponse;
}

export const authClient = {
  async login(username: string, password: string): Promise<TokenSet> {
    const form = new URLSearchParams({
      grant_type: 'password',
      client_id: appConfig.clientId,
      username,
      password,
      scope: appConfig.scopes.join(' '),
    });
    return toTokenSet(await postToken(form));
  },

  async refresh(refreshToken: string): Promise<TokenSet> {
    const form = new URLSearchParams({
      grant_type: 'refresh_token',
      client_id: appConfig.clientId,
      refresh_token: refreshToken,
    });
    return toTokenSet(await postToken(form));
  },
};
