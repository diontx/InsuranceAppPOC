import { Pipe, PipeTransform } from '@angular/core';

/**
 * Parses an API timestamp as UTC.
 *
 * The API's `...Utc` fields are UTC, but SQL Server `datetime2` carries no offset,
 * so EF Core materialises them with an unspecified kind and they serialise
 * WITHOUT a trailing `Z`. `new Date('2026-09-13T10:00:00')` reads that as LOCAL
 * time, which would shift every "time in queue" by the browser's UTC offset. The
 * field name is the contract, so a missing designator is read as UTC.
 */
export function parseUtc(value: string | null | undefined): Date | null {
  if (!value) {
    return null;
  }

  const hasOffset = /(?:[zZ]|[+-]\d{2}:?\d{2})$/.test(value);
  const date = new Date(hasOffset ? value : `${value}Z`);

  return Number.isNaN(date.getTime()) ? null : date;
}

/** `{{ item.submittedUtc | utc | date: 'medium' }}` */
@Pipe({ name: 'utc' })
export class UtcPipe implements PipeTransform {
  transform(value: string | null | undefined): Date | null {
    return parseUtc(value);
  }
}
