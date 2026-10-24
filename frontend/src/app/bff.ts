import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, tap, throwError } from 'rxjs';

const key = 'bff-sign-in-at';

/**
 * The backend for the browser (ADR 0003): when /api/v1 answers 401 with the address where a browser signs in to it, the page goes
 * there and comes back to where it was; the program's own session makes that a few quick redirects. Once in fifteen seconds at most
 * until an API answer succeeds again, so a sign-in that gives no working cookie shows the error instead of looping.
 */
export const bffSignIn: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    tap(() => {
      if (req.url.startsWith('/api/v1/'))
        try {
          sessionStorage.removeItem(key);
        } catch {
          /* storage unavailable: the guard is then off */
        }
    }),
    catchError((e: HttpErrorResponse) => {
      const signIn: unknown = e.error?.signIn;
      if (
        e.status === 401 &&
        req.url.startsWith('/api/v1/') &&
        typeof signIn === 'string' &&
        signIn.startsWith('/')
      ) {
        let recent = false;
        try {
          recent = Date.now() - Number(sessionStorage.getItem(key) ?? 0) < 15000;
          if (!recent) sessionStorage.setItem(key, String(Date.now()));
        } catch {
          recent = false;
        }
        if (!recent)
          location.assign(
            signIn +
              '?returnUrl=' +
              encodeURIComponent(location.pathname + location.search + location.hash),
          );
      }
      return throwError(() => e);
    }),
  );
