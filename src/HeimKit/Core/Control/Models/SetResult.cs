namespace HeimKit.Core.Control.Models;

internal enum SetResult { None, Applied, Pending, Busy, NotAdmin, AdminChangesDisabled, UnknownSwitch, RateLimited, Unavailable, Failed, InvalidValue }
