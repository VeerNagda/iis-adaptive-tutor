const BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5242";

type HttpMethod = "GET" | "POST" | "PUT" | "DELETE" | "PATCH";

interface RequestOptions {
  method?: HttpMethod;
  body?: unknown;
  headers?: Record<string, string>;
}

async function request<T>(endpoint: string, options: RequestOptions = {}): Promise<T> {
  const { method = "GET", body, headers = {} } = options;

  const config: RequestInit = {
    method,
    headers: {
      "Content-Type": "application/json",
      ...headers,
    },
  };

  if (body !== undefined) {
    config.body = JSON.stringify(body);
  }

  const response = await fetch(`${BASE_URL}${endpoint}`, config);

  if (!response.ok) {
    const errorText = await response.text();
    throw new Error(`HTTP ${response.status}: ${errorText}`);
  }

  // Handle empty responses (e.g. 204 No Content)
  const contentType = response.headers.get("content-type");
  if (!contentType?.includes("application/json")) {
    return undefined as T;
  }

  return response.json() as Promise<T>;
}

export const apiService = {
  get<T>(endpoint: string, headers?: Record<string, string>) {
    return request<T>(endpoint, { method: "GET", headers });
  },

  post<T>(endpoint: string, body: unknown, headers?: Record<string, string>) {
    return request<T>(endpoint, { method: "POST", body, headers });
  },

  put<T>(endpoint: string, body: unknown, headers?: Record<string, string>) {
    return request<T>(endpoint, { method: "PUT", body, headers });
  },

  patch<T>(endpoint: string, body: unknown, headers?: Record<string, string>) {
    return request<T>(endpoint, { method: "PATCH", body, headers });
  },

  delete<T>(endpoint: string, headers?: Record<string, string>) {
    return request<T>(endpoint, { method: "DELETE", headers });
  },
};
