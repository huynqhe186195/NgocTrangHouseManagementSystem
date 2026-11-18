import { useEffect, useState } from 'react';
import { Alert } from '../../components/ui/Alert';
import { Button } from '../../components/ui/Button';
import { Dialog } from '../../components/ui/Dialog';
import { Field } from '../../components/ui/Field';
import { SelectField } from '../../components/ui/SelectField';
import { describeMutationError } from '../../lib/api/mutationErrors';
import {
  useBuildingPublicInfo,
  useUpdateBuildingPublicInfo,
} from '../../lib/api/buildingQueries';
import type { HouseRuleIcon, HouseRuleItem } from '../../lib/api/types';

/** Mirrors the server's HouseRuleIcon enum; the server rejects anything else. */
const ICON_OPTIONS: { value: string; label: string }[] = [
  { value: '1', label: 'Cấm (biểu tượng gạch chéo)' },
  { value: '2', label: 'Giờ giấc' },
  { value: '3', label: 'Vệ sinh' },
  { value: '4', label: 'Chung' },
];

interface RuleDraft {
  icon: string;
  text: string;
}

const EMPTY_RULE: RuleDraft = { icon: '1', text: '' };

export function BuildingPublicInfoDialog({
  open,
  buildingId,
  onClose,
}: {
  open: boolean;
  buildingId: string;
  onClose: () => void;
}) {
  const query = useBuildingPublicInfo(open ? buildingId : undefined);
  const update = useUpdateBuildingPublicInfo(buildingId);

  const [electricity, setElectricity] = useState('');
  const [water, setWater] = useState('');
  const [serviceFee, setServiceFee] = useState('');
  const [otherInfo, setOtherInfo] = useState('');
  const [rules, setRules] = useState<RuleDraft[]>([]);
  const [touched, setTouched] = useState(false);

  useEffect(() => {
    if (!open) {
      return;
    }
    const info = query.data;
    setElectricity(info?.electricityUnitPrice == null ? '' : String(info.electricityUnitPrice));
    setWater(info?.waterUnitPrice == null ? '' : String(info.waterUnitPrice));
    setServiceFee(info?.serviceFee == null ? '' : String(info.serviceFee));
    setOtherInfo(info?.otherInfo ?? '');
    setRules(
      (info?.houseRules ?? []).map((rule) => ({ icon: String(rule.icon), text: rule.text })),
    );
    setTouched(false);
    update.reset();
    // Re-seed only when the dialog opens or the server value actually changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, query.data]);

  const view = describeMutationError(update.error, 'building', 'edit');

  function numericError(label: string, raw: string): string | undefined {
    if (raw.trim().length === 0) {
      return undefined;
    }
    const parsed = Number(raw);
    if (!Number.isFinite(parsed) || parsed < 0 || parsed > 1_000_000_000) {
      return `${label} phải là số từ 0 đến 1.000.000.000.`;
    }
    return undefined;
  }

  const electricityError = numericError('Giá điện', electricity);
  const waterError = numericError('Giá nước', water);
  const serviceFeeError = numericError('Phí dịch vụ', serviceFee);
  const ruleErrors = rules.map((rule) => {
    if (rule.text.trim().length === 0) {
      return 'Mỗi quy định cần có nội dung.';
    }
    if (rule.text.trim().length > 200) {
      return 'Nội dung tối đa 200 ký tự.';
    }
    return undefined;
  });

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (update.isPending) {
      return;
    }
    setTouched(true);
    if (
      electricityError ||
      waterError ||
      serviceFeeError ||
      ruleErrors.some(Boolean)
    ) {
      return;
    }

    const houseRules: HouseRuleItem[] = rules.map((rule) => ({
      icon: Number(rule.icon) as HouseRuleIcon,
      text: rule.text.trim(),
    }));

    update.mutate(
      {
        electricityUnitPrice: electricity.trim().length === 0 ? null : Number(electricity),
        waterUnitPrice: water.trim().length === 0 ? null : Number(water),
        serviceFee: serviceFee.trim().length === 0 ? null : Number(serviceFee),
        otherInfo: otherInfo.trim().length === 0 ? null : otherInfo.trim(),
        houseRules,
      },
      { onSuccess: onClose },
    );
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      busy={update.isPending}
      size="lg"
      title="Giá và nội quy công bố"
      description="Thông tin này hiện cho khách thuê xem. Nó không thay đổi giá dùng để tính hoá đơn."
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={update.isPending}>
            Huỷ
          </Button>
          <Button type="submit" form="public-info-form" loading={update.isPending}>
            Lưu thông tin
          </Button>
        </>
      }
    >
      <form
        id="public-info-form"
        onSubmit={handleSubmit}
        className="flex flex-col gap-4"
        noValidate
      >
        {view && (
          <Alert tone="error">
            {view.formError}
            {view.traceId && (
              <span className="mt-1 block font-mono text-[11px]">traceId: {view.traceId}</span>
            )}
          </Alert>
        )}

        {query.error && <Alert tone="error">Không tải được thông tin đã công bố.</Alert>}

        <div className="grid gap-4 sm:grid-cols-3">
          <Field
            label="Điện (đ/kWh)"
            type="number"
            step={1}
            min={0}
            value={electricity}
            onChange={(event) => setElectricity(event.target.value)}
            error={view?.fieldErrors.electricityUnitPrice ?? (touched ? electricityError : undefined)}
            hint="Để trống nếu chưa công bố."
          />
          <Field
            label="Nước (đ/người/tháng)"
            type="number"
            step={1}
            min={0}
            value={water}
            onChange={(event) => setWater(event.target.value)}
            error={view?.fieldErrors.waterUnitPrice ?? (touched ? waterError : undefined)}
            hint="Để trống nếu chưa công bố."
          />
          <Field
            label="Phí dịch vụ (đ/phòng/tháng)"
            type="number"
            step={1}
            min={0}
            value={serviceFee}
            onChange={(event) => setServiceFee(event.target.value)}
            error={view?.fieldErrors.serviceFee ?? (touched ? serviceFeeError : undefined)}
            hint="Để trống nếu chưa công bố."
          />
        </div>

        <Field
          label="Thông tin khác"
          maxLength={1000}
          value={otherInfo}
          onChange={(event) => setOtherInfo(event.target.value)}
          error={view?.fieldErrors.otherInfo}
          hint="Ví dụ: Gửi xe máy miễn phí tối đa 2 xe. Không bắt buộc."
        />

        <div className="rounded-lg border border-outline-variant">
          <div className="flex items-center justify-between gap-3 border-b border-outline-variant px-3 py-2.5">
            <p className="text-sm font-semibold text-on-surface">Quy định nhà trọ</p>
            <button
              type="button"
              onClick={() => setRules((current) => [...current, { ...EMPTY_RULE }])}
              className="inline-flex items-center gap-1 rounded-lg px-2 py-1 text-xs font-semibold text-primary transition hover:bg-surface-low focus:outline-none focus-visible:ring-3 focus-visible:ring-primary/25"
            >
              <span className="material-symbols-outlined text-base">add</span>
              Thêm quy định
            </button>
          </div>

          {rules.length === 0 ? (
            <p className="px-3 py-4 text-sm text-on-surface-variant">
              Chưa có quy định nào. Thêm dòng đầu tiên nếu muốn công bố.
            </p>
          ) : (
            <>
              <div className="hidden gap-2 border-b border-outline-variant px-3 py-2 sm:flex">
                <span className="w-56 text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
                  Biểu tượng
                </span>
                <span className="flex-1 text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
                  Nội dung quy định
                </span>
                <span className="w-9" aria-hidden />
              </div>
              <ul className="flex flex-col gap-3 p-3">
                {rules.map((rule, index) => (
                  <li key={index} className="flex flex-col gap-2 sm:flex-row sm:items-start">
                    <div className="w-full sm:w-56">
                      <SelectField
                        label={`Biểu tượng quy định ${index + 1}`}
                        hideLabel
                        options={ICON_OPTIONS}
                        value={rule.icon}
                        onChange={(event) =>
                          setRules((current) =>
                            current.map((item, position) =>
                              position === index ? { ...item, icon: event.target.value } : item,
                            ),
                          )
                        }
                      />
                    </div>
                    <div className="min-w-0 flex-1">
                      <input
                        aria-label={`Nội dung quy định ${index + 1}`}
                        aria-invalid={touched && ruleErrors[index] ? true : undefined}
                        maxLength={200}
                        value={rule.text}
                        onChange={(event) =>
                          setRules((current) =>
                            current.map((item, position) =>
                              position === index ? { ...item, text: event.target.value } : item,
                            ),
                          )
                        }
                        placeholder="Ví dụ: Không nuôi thú cưng"
                        className={
                          'h-10 w-full rounded-[0.5rem] border bg-surface-lowest px-3 text-sm text-on-surface ' +
                          'placeholder:text-outline focus:outline-none focus:ring-3 ' +
                          (touched && ruleErrors[index]
                            ? 'border-error focus:border-error focus:ring-error/15'
                            : 'border-outline-variant focus:border-primary focus:ring-primary/15')
                        }
                      />
                      {touched && ruleErrors[index] && (
                        <p className="mt-1 text-xs text-error">{ruleErrors[index]}</p>
                      )}
                    </div>
                    <button
                      type="button"
                      aria-label={`Xoá quy định ${index + 1}`}
                      onClick={() =>
                        setRules((current) => current.filter((_, position) => position !== index))
                      }
                      className="self-end rounded-lg p-2 text-on-surface-variant transition hover:bg-surface-low hover:text-error focus:outline-none focus-visible:ring-3 focus-visible:ring-primary/25 sm:self-start"
                    >
                      <span className="material-symbols-outlined text-lg">delete</span>
                    </button>
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>
      </form>
    </Dialog>
  );
}
