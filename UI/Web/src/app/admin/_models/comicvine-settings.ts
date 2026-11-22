export interface ComicVineHealth {
  configured: boolean;
  requestsLastHour: number;
  requestLimit: number;
  lastSuccess?: string;
  lastFailure?: string;
  failure?: string;
  retryAt?: string;
}

export interface ComicVineSettings {
  hasApiKey: boolean;
  hasSavedApiKey: boolean;
  usesEnvironmentKey: boolean;
  requiresReplacement: boolean;
}

export interface ComicVineConnectionResult {
  success: boolean;
}
