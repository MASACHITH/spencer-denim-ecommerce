import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** Response of `GET /api/ping` (SpencerDenim.Application.Diagnostics.Ping.PingResponse). */
export interface PingResponse {
  status: string;
  service: string;
  version: string;
  serverTimeUtc: string;
}

@Injectable({ providedIn: 'root' })
export class PingApi {
  static readonly url = '/api/ping';

  private readonly http = inject(HttpClient);

  ping(): Observable<PingResponse> {
    return this.http.get<PingResponse>(PingApi.url);
  }
}
