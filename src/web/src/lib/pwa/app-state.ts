/**
 * Task J1: whether the signed-in person's device may keep unsent work (the tenant's offline data
 * setting, decision 13), set by AppRuntime on every page and read by the forms that queue work.
 */
let allowed = false;

export function setOfflineWorkAllowed(value: boolean): void {
  allowed = value;
}

export function offlineWorkAllowed(): boolean {
  return allowed;
}
