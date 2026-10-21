/**
 * Overseas delivery and the Payment methods switch, for the admin stub (owner decision 2026-10-21: Sri Lanka plus
 * worldwide delivery; bank transfer switched off and PayPal the only payment method, "block only for now").
 *
 * **Field for field from the real DTOs** — `OverseasDeliveryAdminDto` with `DeliveryRegionAdminDto` and
 * `CountryDeliveryAdminDto`, and `PaymentProvidersAdminDto` with `PaymentProviderAdminDto` — and **no more
 * permissive than the real validators**: `OverseasDeliveryRules` (fee 0 to 1,000,000 to the cent, null only on a
 * region; days 1 to 90, fewest no more than most, a country's both or neither), the switches' required booleans,
 * the row-version rule (a missing token is a 409; a stale one is a 409 when the save would change something,
 * because the real store's optimistic `UPDATE` only runs then), and `PaymentProviderSwitchHandler`'s refusal to
 * switch on a method that is not ready. Issue #126: Playwright never starts the .NET API, so a stub looser than
 * the API is a screen that can regress without a browser test noticing.
 *
 * **Permissions**, as the real routes enforce them: `Settings.View` to read the overseas screen, `Settings.Edit`
 * to change it, `Settings.Payments` (Super Admin only) for the payment methods. The role is per test state, set
 * with `/__test__/delivery-role?role=super|manager|viewer|none`.
 */

const SEEDED_REGIONS = [
  ['ASIA', 'Asia', 6500, 5, 10],
  ['MIDDLE_EAST', 'Middle East', 7500, 5, 10],
  ['EUROPE', 'Europe', 9500, 7, 14],
  ['NORTH_AMERICA', 'North America', 10500, 7, 14],
  ['OCEANIA', 'Oceania', 9500, 7, 14],
  ['REST_OF_WORLD', 'Rest of world', 12500, 10, 21],
];

/** A representative slice of `ref.Countries`, by name, Sri Lanka excluded as the real screen excludes it. */
const SEEDED_COUNTRIES = [
  ['AQ', 'Antarctica', 'REST_OF_WORLD', false, false, false],
  ['AU', 'Australia', 'OCEANIA', true, true, true],
  ['CA', 'Canada', 'NORTH_AMERICA', true, true, true],
  ['FR', 'France', 'EUROPE', true, false, true],
  ['DE', 'Germany', 'EUROPE', true, false, true],
  ['IN', 'India', 'ASIA', true, false, true],
  ['JP', 'Japan', 'ASIA', true, false, true],
  ['MV', 'Maldives', 'ASIA', false, false, true],
  ['QA', 'Qatar', 'MIDDLE_EAST', false, false, true],
  ['AE', 'United Arab Emirates', 'MIDDLE_EAST', false, false, true],
  ['GB', 'United Kingdom', 'EUROPE', true, false, true],
  ['US', 'United States', 'NORTH_AMERICA', true, true, true],
];

const ROLES = {
  super: ['Settings.View', 'Settings.Edit', 'Settings.Payments'],
  manager: ['Settings.View', 'Settings.Edit'],
  viewer: ['Settings.View'],
  none: [],
};

let tick = 500;

function nextVersion() {
  tick += 1;
  return Buffer.from([0, 0, 0, 0, 0, 0, tick >> 8, tick & 255]).toString('base64');
}

export function freshDeliveryState() {
  return {
    role: 'super',
    internationalEnabled: true,
    internationalRowVersion: nextVersion(),
    regions: SEEDED_REGIONS.map(([code, name, fee, min, max]) => ({
      code,
      name,
      fee,
      estimatedDaysMin: min,
      estimatedDaysMax: max,
      isEnabled: true,
      rowVersion: nextVersion(),
    })),
    countries: SEEDED_COUNTRIES.map(([code, name, regionCode, postal, region, enabled]) => ({
      code,
      name,
      regionCode,
      isShippingEnabled: enabled,
      deliveryFeeOverride: null,
      estimatedDaysMinOverride: null,
      estimatedDaysMaxOverride: null,
      postalCodeRequired: postal,
      regionRequired: region,
      rowVersion: nextVersion(),
    })),
    providers: [
      { code: 'BankTransfer', displayName: 'Bank or online transfer', isEnabled: false, isReady: true, rowVersion: nextVersion() },
      { code: 'PayPal', displayName: 'PayPal', isEnabled: false, isReady: false, rowVersion: nextVersion() },
    ],
  };
}

/** `OverseasDeliveryRate.Resolve`, in the same order. */
function quote(delivery, country) {
  const region = delivery.regions.find((candidate) => candidate.code === country.regionCode);

  if (!delivery.internationalEnabled) {
    return { refusal: 'MethodDisabled' };
  }

  if (!country.isShippingEnabled) {
    return { refusal: 'CountryDisabled' };
  }

  if (!region || !region.isEnabled) {
    return { refusal: 'RegionDisabled' };
  }

  const fee = country.deliveryFeeOverride ?? region.fee;
  if (fee === null || fee < 0) {
    return { refusal: 'NotPriced' };
  }

  const own = country.estimatedDaysMinOverride !== null && country.estimatedDaysMaxOverride !== null;

  return {
    refusal: 'None',
    fee,
    min: own ? country.estimatedDaysMinOverride : region.estimatedDaysMin,
    max: own ? country.estimatedDaysMaxOverride : region.estimatedDaysMax,
  };
}

function screen(delivery) {
  return {
    internationalEnabled: delivery.internationalEnabled,
    internationalRowVersion: delivery.internationalRowVersion,
    regions: delivery.regions.map((region) => ({
      code: region.code,
      name: region.name,
      fee: region.fee,
      estimatedDaysMin: region.estimatedDaysMin,
      estimatedDaysMax: region.estimatedDaysMax,
      isEnabled: region.isEnabled,
      countryCount: delivery.countries.filter((country) => country.regionCode === region.code).length,
      rowVersion: region.rowVersion,
    })),
    countries: [...delivery.countries]
      .sort((left, right) => left.name.localeCompare(right.name))
      .map((country) => {
        const offered = quote(delivery, country);
        const available = offered.refusal === 'None';

        return {
          code: country.code,
          name: country.name,
          regionCode: country.regionCode,
          isShippingEnabled: country.isShippingEnabled,
          deliveryFeeOverride: country.deliveryFeeOverride,
          estimatedDaysMinOverride: country.estimatedDaysMinOverride,
          estimatedDaysMaxOverride: country.estimatedDaysMaxOverride,
          postalCodeRequired: country.postalCodeRequired,
          regionRequired: country.regionRequired,
          isAvailable: available,
          effectiveFee: available ? offered.fee : null,
          effectiveDaysMin: available ? offered.min : null,
          effectiveDaysMax: available ? offered.max : null,
          refusal: offered.refusal,
          rowVersion: country.rowVersion,
        };
      }),
  };
}

function panel(delivery) {
  const providers = delivery.providers.map((provider) => ({
    code: provider.code,
    displayName: provider.displayName,
    isEnabled: provider.isEnabled,
    isReady: provider.isReady,
    isOffered: provider.isEnabled && provider.isReady,
    rowVersion: provider.rowVersion,
  }));

  return { providers, anyOffered: providers.some((provider) => provider.isOffered) };
}

function holds(delivery, permission) {
  return (ROLES[delivery.role] ?? []).includes(permission);
}

/** `OverseasDeliveryRules.CheckFee`. */
function feeProblem(fee) {
  if (fee === null) {
    return null;
  }

  if (fee < 0 || fee > 1_000_000) {
    return 'Enter a fee from Rs. 0 to Rs. 1,000,000.';
  }

  return Math.round(fee * 100) / 100 !== fee ? 'A fee can have at most two decimal places.' : null;
}

/** `OverseasDeliveryRules.CheckDays`. */
function daysProblem(min, max, required) {
  if (min === null && max === null) {
    return required ? 'Enter the estimated delivery days.' : null;
  }

  if (min === null || max === null) {
    return 'Enter both the fewest and the most days, or neither.';
  }

  return min < 1 || max > 90 || min > max
    ? 'Enter days from 1 to 90, with the fewest no more than the most.'
    : null;
}

/**
 * What `ReadFromJsonAsync` would bind, or `undefined` when it would refuse the body as malformed: a number
 * where a decimal is wanted, an integer where an int is, a boolean where a bool is, and null for each.
 */
function field(body, name, kind) {
  if (!Object.hasOwn(body, name) || body[name] === null) {
    return { ok: true, value: null };
  }

  const value = body[name];
  const ok =
    (kind === 'decimal' && typeof value === 'number' && Number.isFinite(value)) ||
    (kind === 'int' && Number.isInteger(value)) ||
    (kind === 'bool' && typeof value === 'boolean') ||
    (kind === 'string' && typeof value === 'string');

  return { ok, value };
}

function validation(response, json, errors) {
  json(response, 400, {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
    title: 'One or more validation errors occurred.',
    status: 400,
    errors,
  });
}

function malformed(problem, response) {
  problem(response, 400, 'Malformed JSON', 'The request body is not valid JSON for this endpoint.', 'malformed_json');
}

function stale(problem, response, missing) {
  problem(
    response,
    409,
    'Changed by somebody else',
    missing
      ? 'This request did not carry the row’s current version, so it cannot be applied safely. Reload the page.'
      : 'Somebody else changed this while you were editing it, so nothing was saved. Reload the page and try again.',
    'stale_row_version',
  );
}

export async function handleDeliveryRoute({
  path,
  method,
  request,
  response,
  state,
  json,
  problem,
  readBody,
  suppliedRowVersion,
}) {
  if (path === '/__test__/delivery-role') {
    state.delivery ??= freshDeliveryState();
    const role = new URL(request.url, 'http://localhost').searchParams.get('role') ?? 'super';
    state.delivery.role = Object.hasOwn(ROLES, role) ? role : 'none';
    json(response, 200, { role: state.delivery.role });
    return true;
  }

  if (path === '/__test__/paypal-ready') {
    state.delivery ??= freshDeliveryState();
    const ready = new URL(request.url, 'http://localhost').searchParams.get('ready') === 'true';
    state.delivery.providers.find((provider) => provider.code === 'PayPal').isReady = ready;
    json(response, 200, { ready });
    return true;
  }

  const overseas = path === '/api/admin/delivery/overseas' || path.startsWith('/api/admin/delivery/overseas/');
  const payments = path === '/api/admin/settings/payment-providers' || path.startsWith('/api/admin/settings/payment-providers/');

  if (!overseas && !payments) {
    return false;
  }

  state.delivery ??= freshDeliveryState();
  const delivery = state.delivery;

  // ---- the Payment methods switch -------------------------------------------------------------
  if (payments) {
    if (!holds(delivery, 'Settings.Payments')) {
      json(response, 403, { title: 'Forbidden', status: 403 });
      return true;
    }

    if (path === '/api/admin/settings/payment-providers' && method === 'GET') {
      json(response, 200, panel(delivery));
      return true;
    }

    const match = /^\/api\/admin\/settings\/payment-providers\/([^/]+)$/.exec(path);
    if (!match || method !== 'PUT') {
      json(response, 405, { title: 'Method not allowed', status: 405 });
      return true;
    }

    const body = await readBody(request);
    if (body === undefined || body === null || typeof body !== 'object') {
      malformed(problem, response);
      return true;
    }

    const isEnabled = field(body, 'isEnabled', 'bool');
    const version = field(body, 'rowVersion', 'string');
    if (!isEnabled.ok || !version.ok) {
      malformed(problem, response);
      return true;
    }

    if (isEnabled.value === null) {
      validation(response, json, { isEnabled: ['Say whether this payment method is switched on.'] });
      return true;
    }

    const supplied = suppliedRowVersion(request, body);
    if (!supplied) {
      stale(problem, response, true);
      return true;
    }

    const provider = delivery.providers.find((candidate) => candidate.code === decodeURIComponent(match[1]).trim());
    if (!provider) {
      problem(response, 404, 'No such payment method', 'There is no payment method with that code.', 'not_found');
      return true;
    }

    if (isEnabled.value && !provider.isEnabled && !provider.isReady) {
      validation(response, json, {
        isEnabled: [
          `${provider.displayName} is not ready to take payments yet. Finish its settings first (for PayPal: the client ID, secret, webhook ID and today's exchange rate).`,
        ],
      });
      return true;
    }

    if (provider.isEnabled !== isEnabled.value) {
      if (supplied !== provider.rowVersion) {
        stale(problem, response, false);
        return true;
      }

      provider.isEnabled = isEnabled.value;
      provider.rowVersion = nextVersion();
    }

    json(response, 200, panel(delivery));
    return true;
  }

  // ---- the Overseas delivery screen -----------------------------------------------------------
  if (method === 'GET' && path === '/api/admin/delivery/overseas') {
    if (!holds(delivery, 'Settings.View')) {
      json(response, 403, { title: 'Forbidden', status: 403 });
      return true;
    }

    json(response, 200, screen(delivery));
    return true;
  }

  if (method !== 'PUT') {
    json(response, 405, { title: 'Method not allowed', status: 405 });
    return true;
  }

  if (!holds(delivery, 'Settings.Edit')) {
    json(response, 403, { title: 'Forbidden', status: 403 });
    return true;
  }

  const body = await readBody(request);
  if (body === undefined || body === null || typeof body !== 'object') {
    malformed(problem, response);
    return true;
  }

  const supplied = suppliedRowVersion(request, body);

  if (path === '/api/admin/delivery/overseas/international') {
    const isEnabled = field(body, 'isEnabled', 'bool');
    if (!isEnabled.ok) {
      malformed(problem, response);
      return true;
    }

    if (isEnabled.value === null) {
      validation(response, json, { isEnabled: ['Say whether International delivery is switched on.'] });
      return true;
    }

    if (!supplied) {
      stale(problem, response, true);
      return true;
    }

    if (delivery.internationalEnabled !== isEnabled.value) {
      if (supplied !== delivery.internationalRowVersion) {
        stale(problem, response, false);
        return true;
      }

      delivery.internationalEnabled = isEnabled.value;
      delivery.internationalRowVersion = nextVersion();
    }

    json(response, 200, screen(delivery));
    return true;
  }

  const regionRoute = /^\/api\/admin\/delivery\/overseas\/regions\/([^/]+)$/.exec(path);
  if (regionRoute) {
    const fee = field(body, 'fee', 'decimal');
    const min = field(body, 'estimatedDaysMin', 'int');
    const max = field(body, 'estimatedDaysMax', 'int');
    const isEnabled = field(body, 'isEnabled', 'bool');
    if (!fee.ok || !min.ok || !max.ok || !isEnabled.ok) {
      malformed(problem, response);
      return true;
    }

    const errors = {};
    const feeError = feeProblem(fee.value);
    const daysError = daysProblem(min.value, max.value, true);
    if (feeError) {
      errors.fee = [feeError];
    }

    if (daysError) {
      errors.estimatedDays = [daysError];
    }

    if (isEnabled.value === null) {
      errors.isEnabled = ['Say whether this region is switched on.'];
    }

    if (Object.keys(errors).length > 0) {
      validation(response, json, errors);
      return true;
    }

    if (!supplied) {
      stale(problem, response, true);
      return true;
    }

    const region = delivery.regions.find((candidate) => candidate.code === decodeURIComponent(regionRoute[1]).trim());
    if (!region) {
      problem(response, 404, 'Not found', 'There is no overseas region or country with that code.', 'not_found');
      return true;
    }

    const changed =
      region.fee !== fee.value ||
      region.estimatedDaysMin !== min.value ||
      region.estimatedDaysMax !== max.value ||
      region.isEnabled !== isEnabled.value;

    if (changed) {
      if (supplied !== region.rowVersion) {
        stale(problem, response, false);
        return true;
      }

      Object.assign(region, {
        fee: fee.value,
        estimatedDaysMin: min.value,
        estimatedDaysMax: max.value,
        isEnabled: isEnabled.value,
        rowVersion: nextVersion(),
      });
    }

    json(response, 200, screen(delivery));
    return true;
  }

  const countryRoute = /^\/api\/admin\/delivery\/overseas\/countries\/([^/]+)$/.exec(path);
  if (countryRoute) {
    const enabled = field(body, 'isShippingEnabled', 'bool');
    const fee = field(body, 'deliveryFeeOverride', 'decimal');
    const min = field(body, 'estimatedDaysMinOverride', 'int');
    const max = field(body, 'estimatedDaysMaxOverride', 'int');
    if (!enabled.ok || !fee.ok || !min.ok || !max.ok) {
      malformed(problem, response);
      return true;
    }

    const errors = {};
    const feeError = feeProblem(fee.value);
    const daysError = daysProblem(min.value, max.value, false);
    if (feeError) {
      errors.deliveryFeeOverride = [feeError];
    }

    if (daysError) {
      errors.estimatedDaysOverride = [daysError];
    }

    if (enabled.value === null) {
      errors.isShippingEnabled = ['Say whether this country is delivered to.'];
    }

    if (Object.keys(errors).length > 0) {
      validation(response, json, errors);
      return true;
    }

    if (!supplied) {
      stale(problem, response, true);
      return true;
    }

    const raw = decodeURIComponent(countryRoute[1]).trim();
    const code = /^[A-Za-z]{2}$/.test(raw) ? raw.toUpperCase() : null;
    const country = code === null || code === 'LK' ? undefined : delivery.countries.find((candidate) => candidate.code === code);
    if (!country) {
      problem(response, 404, 'Not found', 'There is no overseas region or country with that code.', 'not_found');
      return true;
    }

    const changed =
      country.isShippingEnabled !== enabled.value ||
      country.deliveryFeeOverride !== fee.value ||
      country.estimatedDaysMinOverride !== min.value ||
      country.estimatedDaysMaxOverride !== max.value;

    if (changed) {
      if (supplied !== country.rowVersion) {
        stale(problem, response, false);
        return true;
      }

      Object.assign(country, {
        isShippingEnabled: enabled.value,
        deliveryFeeOverride: fee.value,
        estimatedDaysMinOverride: min.value,
        estimatedDaysMaxOverride: max.value,
        rowVersion: nextVersion(),
      });
    }

    json(response, 200, screen(delivery));
    return true;
  }

  json(response, 404, { title: 'Not found', status: 404 });
  return true;
}
