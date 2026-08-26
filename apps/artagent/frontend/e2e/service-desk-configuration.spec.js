import { expect, test } from '@playwright/test';
import {
  buildScenariosResponse,
  installApiMocks,
  makeScenario,
} from './helpers/scenario-mocks.js';

const SERVICE_DESK = makeScenario({
  name: 'service_desk',
  icon: '🎧',
  start_agent: 'ServiceDeskIntakeAgent',
  agents: ['ServiceDeskIntakeAgent', 'StandbyConfirmationAgent'],
  is_active: true,
});

const initialConfiguration = () => ({
  revision: 1,
  retry_intervals_minutes: [10],
  services: [
    {
      service_id: 'email',
      name: 'Email',
      phone_number: '+14255550201',
    },
    {
      service_id: 'vpn',
      name: 'VPN',
      phone_number: '+14255550204',
    },
  ],
  created_at: '2026-08-25T12:00:00Z',
  updated_at: '2026-08-25T12:00:00Z',
});

async function installConfigurationMock(
  page,
  { conflictOnce = false, serviceInUseOnce = false } = {},
) {
  const state = {
    configuration: initialConfiguration(),
    lastUpdate: null,
    conflictOnce,
    serviceInUseOnce,
  };
  await page.route('**/api/v1/service-desk/configuration', async (route) => {
    const request = route.request();
    if (request.method() === 'GET') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(state.configuration),
      });
      return;
    }
    if (request.method() === 'PUT') {
      state.lastUpdate = request.postDataJSON();
      if (state.conflictOnce) {
        state.conflictOnce = false;
        state.configuration = {
          ...state.configuration,
          revision: 2,
          retry_intervals_minutes: [2, 10],
        };
        await route.fulfill({
          status: 409,
          contentType: 'application/json',
          body: JSON.stringify({
            detail: { code: 'revision_conflict', message: 'Configuration changed.' },
          }),
        });
        return;
      }
      if (state.serviceInUseOnce) {
        state.serviceInUseOnce = false;
        await route.fulfill({
          status: 409,
          contentType: 'application/json',
          body: JSON.stringify({
            detail: {
              code: 'service_in_use',
              message: 'The service is still used by open callback work.',
            },
          }),
        });
        return;
      }
      state.configuration = {
        ...state.configuration,
        ...state.lastUpdate,
        revision: state.configuration.revision + 1,
        services: state.lastUpdate.services.map((service, index) => ({
          ...service,
          service_id: service.service_id || `svc-${index}`,
        })),
        updated_at: '2026-08-25T12:05:00Z',
      };
      delete state.configuration.expected_revision;
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(state.configuration),
      });
      return;
    }
    await route.fallback();
  });
  return state;
}

async function openSettings(page) {
  await page.goto('/');
  await page.getByTitle('Select Industry Scenario').click();
  await page.getByRole('button', { name: 'Configure Service Desk…' }).click();
  await expect(
    page.getByRole('heading', { name: 'Service Desk settings', exact: true }),
  ).toBeVisible();
}

test.beforeEach(async ({ page }) => {
  await installApiMocks(
    page,
    buildScenariosResponse({
      builtins: [SERVICE_DESK],
      activeScenario: 'service_desk',
    }),
  );
});

test('edits retry intervals and service routes', async ({ page }) => {
  const state = await installConfigurationMock(page);
  await openSettings(page);

  await page.getByLabel('Retry intervals in minutes').fill('1;2;5;10;30');
  await page.getByLabel('Service name 1').fill('Messaging');
  await page.getByLabel('Service call number 1').fill('+14255550999');
  await page.getByRole('button', { name: 'Add service' }).click();
  await page.getByLabel('Service name 3').fill('Identity Platform');
  await page.getByLabel('Service call number 3').fill('+14255550888');
  await page.getByRole('button', { name: 'Save' }).click();

  await expect(page.getByText('Service desk configuration saved.')).toBeVisible();
  expect(state.lastUpdate.retry_intervals_minutes).toEqual([1, 2, 5, 10, 30]);
  expect(state.lastUpdate.services[0]).toMatchObject({
    service_id: 'email',
    name: 'Messaging',
    phone_number: '+14255550999',
  });
  expect(state.lastUpdate.services[2]).toMatchObject({
    service_id: null,
    name: 'Identity Platform',
    phone_number: '+14255550888',
  });
});

test('validates retry intervals before saving', async ({ page }) => {
  const state = await installConfigurationMock(page);
  await openSettings(page);

  await page.getByLabel('Retry intervals in minutes').fill('0;2');
  await page.getByRole('button', { name: 'Save' }).click();

  await expect(page.getByText('Retry intervals must be whole minutes from 1 to 1440.')).toBeVisible();
  expect(state.lastUpdate).toBeNull();
});

test('reloads the latest values after a revision conflict', async ({ page }) => {
  await installConfigurationMock(page, { conflictOnce: true });
  await openSettings(page);

  await page.getByLabel('Retry intervals in minutes').fill('1;5');
  await page.getByRole('button', { name: 'Save' }).click();

  await expect(
    page.getByText('Another administrator saved changes first. The latest values were reloaded.'),
  ).toBeVisible();
  await expect(page.getByLabel('Retry intervals in minutes')).toHaveValue('2;10');
});

test('preserves edits when a service cannot be removed from open work', async ({ page }) => {
  await installConfigurationMock(page, { serviceInUseOnce: true });
  await openSettings(page);

  await page.getByLabel('Retry intervals in minutes').fill('1;5');
  await page.getByRole('button', { name: 'Remove VPN' }).click();
  await page.getByRole('button', { name: 'Save' }).click();

  await expect(page.getByText('The service is still used by open callback work.')).toBeVisible();
  await expect(page.getByLabel('Retry intervals in minutes')).toHaveValue('1;5');
  await expect(page.getByLabel('Service name 2')).toHaveCount(0);
});
