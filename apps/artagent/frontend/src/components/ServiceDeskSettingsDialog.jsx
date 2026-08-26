import { memo, useCallback, useEffect, useMemo, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  IconButton,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import AddRoundedIcon from '@mui/icons-material/AddRounded';
import CloseRoundedIcon from '@mui/icons-material/CloseRounded';
import DeleteOutlineRoundedIcon from '@mui/icons-material/DeleteOutlineRounded';
import { API_BASE_URL } from '../config/constants.js';

const CONFIGURATION_URL = `${API_BASE_URL}/api/v1/service-desk/configuration`;
const E164_PATTERN = /^\+[1-9]\d{1,14}$/;

const parseRetryIntervals = (value) => {
  const parts = value.split(';').map((part) => part.trim());
  if (parts.some((part) => !part)) {
    throw new Error('Enter retry minutes separated by semicolons, without empty values.');
  }
  if (parts.length > 20) {
    throw new Error('At most 20 retry intervals are allowed.');
  }
  const intervals = parts.map(Number);
  if (
    intervals.some(
      (interval) => !Number.isInteger(interval) || interval < 1 || interval > 1440,
    )
  ) {
    throw new Error('Retry intervals must be whole minutes from 1 to 1440.');
  }
  return intervals;
};

const validateServices = (services) => {
  if (!services.length) {
    throw new Error('At least one service route is required.');
  }
  const names = new Set();
  services.forEach((service) => {
    const name = service.name.trim();
    if (!name) {
      throw new Error('Every service route requires a name.');
    }
    const nameKey = name.toLocaleLowerCase();
    if (names.has(nameKey)) {
      throw new Error(`Service names must be unique: ${name}.`);
    }
    names.add(nameKey);
    if (!E164_PATTERN.test(service.phone_number.trim())) {
      throw new Error(`Enter an E.164 phone number for ${name}, for example +15551234567.`);
    }
  });
};

const ServiceDeskSettingsDialog = memo(function ServiceDeskSettingsDialog({ open, onClose }) {
  const [configuration, setConfiguration] = useState(null);
  const [retryText, setRetryText] = useState('');
  const [services, setServices] = useState([]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  const applyConfiguration = useCallback((value) => {
    setConfiguration(value);
    setRetryText(value.retry_intervals_minutes.join(';'));
    setServices(value.services.map((service) => ({ ...service })));
  }, []);

  const loadConfiguration = useCallback(async ({ conflict = false } = {}) => {
    setLoading(true);
    setError('');
    try {
      const response = await fetch(CONFIGURATION_URL);
      if (!response.ok) {
        throw new Error(`Configuration could not be loaded (HTTP ${response.status}).`);
      }
      applyConfiguration(await response.json());
      if (conflict) {
        setError('Another administrator saved changes first. The latest values were reloaded.');
      }
    } catch (loadError) {
      setError(loadError.message);
    } finally {
      setLoading(false);
    }
  }, [applyConfiguration]);

  useEffect(() => {
    if (open) {
      setSuccess('');
      loadConfiguration();
    }
  }, [loadConfiguration, open]);

  const handleServiceChange = useCallback((index, field, value) => {
    setServices((current) =>
      current.map((service, serviceIndex) =>
        serviceIndex === index ? { ...service, [field]: value } : service,
      ),
    );
    setError('');
    setSuccess('');
  }, []);

  const handleAddService = useCallback(() => {
    setServices((current) => [
      ...current,
      { service_id: null, name: '', phone_number: '' },
    ]);
    setSuccess('');
  }, []);

  const handleRemoveService = useCallback((index) => {
    setServices((current) => current.filter((_, serviceIndex) => serviceIndex !== index));
    setSuccess('');
  }, []);

  const handleSave = useCallback(async () => {
    if (!configuration) return;
    setError('');
    setSuccess('');

    let retryIntervals;
    try {
      retryIntervals = parseRetryIntervals(retryText);
      validateServices(services);
    } catch (validationError) {
      setError(validationError.message);
      return;
    }

    setSaving(true);
    try {
      const response = await fetch(CONFIGURATION_URL, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          expected_revision: configuration.revision,
          retry_intervals_minutes: retryIntervals,
          services: services.map(({ service_id, name, phone_number }) => ({
            service_id,
            name: name.trim(),
            phone_number: phone_number.trim(),
          })),
        }),
      });
      if (response.status === 409) {
        const body = await response.json().catch(() => ({}));
        if (body.detail?.code === 'revision_conflict') {
          await loadConfiguration({ conflict: true });
          return;
        }
        throw new Error(
          body.detail?.message || 'The requested configuration change conflicts with open work.',
        );
      }
      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error(body.detail || `Configuration could not be saved (HTTP ${response.status}).`);
      }
      applyConfiguration(await response.json());
      setSuccess('Service desk configuration saved.');
    } catch (saveError) {
      setError(saveError.message);
    } finally {
      setSaving(false);
    }
  }, [applyConfiguration, configuration, loadConfiguration, retryText, services]);

  const isBusy = loading || saving;
  const revisionLabel = useMemo(
    () => (configuration ? `Revision ${configuration.revision}` : ''),
    [configuration],
  );

  return (
    <Dialog open={open} onClose={isBusy ? undefined : onClose} fullWidth maxWidth="md">
      <DialogTitle sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <Box sx={{ flex: 1 }}>
          <Typography component="h2" variant="h6" sx={{ fontWeight: 700 }}>
            Service Desk settings
          </Typography>
          <Typography variant="caption" color="text.secondary">
            {revisionLabel}
          </Typography>
        </Box>
        <IconButton aria-label="Close Service Desk settings" onClick={onClose} disabled={isBusy}>
          <CloseRoundedIcon />
        </IconButton>
      </DialogTitle>
      <Divider />
      <DialogContent sx={{ display: 'flex', flexDirection: 'column', gap: 2.5 }}>
        {loading && !configuration ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', py: 5 }}>
            <CircularProgress aria-label="Loading Service Desk settings" />
          </Box>
        ) : (
          <>
            {error && <Alert severity="error">{error}</Alert>}
            {success && <Alert severity="success">{success}</Alert>}
            <Box>
              <TextField
                fullWidth
                label="Retry intervals (minutes)"
                value={retryText}
                onChange={(event) => {
                  setRetryText(event.target.value);
                  setError('');
                  setSuccess('');
                }}
                placeholder="1;2;5;10;30"
                helperText="The final value repeats for later attempts. Existing scheduled times are unchanged."
                disabled={isBusy}
                inputProps={{ 'aria-label': 'Retry intervals in minutes' }}
              />
            </Box>
            <Stack spacing={1.5}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                <Typography variant="subtitle1" sx={{ fontWeight: 700, flex: 1 }}>
                  Service routes
                </Typography>
                <Button
                  startIcon={<AddRoundedIcon />}
                  onClick={handleAddService}
                  disabled={isBusy}
                >
                  Add service
                </Button>
              </Box>
              {services.map((service, index) => (
                <Stack
                  key={service.service_id || `new-${index}`}
                  direction={{ xs: 'column', sm: 'row' }}
                  spacing={1}
                  sx={{
                    p: 1.5,
                    border: '1px solid',
                    borderColor: 'divider',
                    borderRadius: 1.5,
                    alignItems: { xs: 'stretch', sm: 'center' },
                  }}
                >
                  <TextField
                    label="Service name"
                    value={service.name}
                    onChange={(event) => handleServiceChange(index, 'name', event.target.value)}
                    disabled={isBusy}
                    fullWidth
                    inputProps={{ 'aria-label': `Service name ${index + 1}` }}
                  />
                  <TextField
                    label="Call number"
                    value={service.phone_number}
                    onChange={(event) =>
                      handleServiceChange(index, 'phone_number', event.target.value)
                    }
                    disabled={isBusy}
                    fullWidth
                    placeholder="+15551234567"
                    inputProps={{ 'aria-label': `Service call number ${index + 1}` }}
                  />
                  <IconButton
                    aria-label={`Remove ${service.name || `service ${index + 1}`}`}
                    color="error"
                    onClick={() => handleRemoveService(index)}
                    disabled={isBusy}
                  >
                    <DeleteOutlineRoundedIcon />
                  </IconButton>
                </Stack>
              ))}
            </Stack>
          </>
        )}
      </DialogContent>
      <Divider />
      <DialogActions sx={{ px: 3, py: 2 }}>
        <Button onClick={onClose} disabled={isBusy}>
          Close
        </Button>
        <Button variant="contained" onClick={handleSave} disabled={isBusy || !configuration}>
          {saving ? 'Saving...' : 'Save'}
        </Button>
      </DialogActions>
    </Dialog>
  );
});

export default ServiceDeskSettingsDialog;
