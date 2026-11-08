// GOXF/DeskBox fork: 「映射整理」 hides mapped desktop entries by setting the
// hidden attribute and records every entry it hid in
// data\mapped-organize.hidden (one absolute path per line). The app restores
// them when mapping ends or DeskBox exits; the uninstaller stops DeskBox
// forcibly, so restore the recorded entries here as well. Entries that were
// hidden before DeskBox touched them are never recorded and stay hidden.
// Included from DeskBox.Uninstall.iss (inside its [Code] section).

const
  DeskBoxMappedOrganizeHiddenLedgerPath = '{localappdata}\DeskBox\data\mapped-organize.hidden';
  DeskBoxFileAttributeHidden = $2;
  DeskBoxInvalidFileAttributes = $FFFFFFFF;

function DeskBoxGetFileAttributes(lpFileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function DeskBoxSetFileAttributes(lpFileName: String; dwFileAttributes: Cardinal): Boolean;
  external 'SetFileAttributesW@kernel32.dll stdcall';

procedure RestoreMappedOrganizeHiddenDesktopItems;
var
  LedgerPath: string;
  Lines: TArrayOfString;
  Index: Integer;
  ItemPath: string;
  Attributes: Cardinal;
  Restored: Integer;
begin
  LedgerPath := ExpandConstant(DeskBoxMappedOrganizeHiddenLedgerPath);
  if not FileExists(LedgerPath) then
    Exit;

  if not LoadStringsFromFile(LedgerPath, Lines) then
  begin
    Log('DeskBox uninstall could not read the mapped-organize hidden ledger.');
    Exit;
  end;

  Restored := 0;
  for Index := 0 to GetArrayLength(Lines) - 1 do
  begin
    ItemPath := Trim(Lines[Index]);
    if ItemPath = '' then
      Continue;

    Attributes := DeskBoxGetFileAttributes(ItemPath);
    if (Attributes <> DeskBoxInvalidFileAttributes) and
       ((Attributes and DeskBoxFileAttributeHidden) <> 0) then
    begin
      if DeskBoxSetFileAttributes(ItemPath, Attributes and (not DeskBoxFileAttributeHidden)) then
        Restored := Restored + 1
      else
        Log('DeskBox uninstall could not restore a hidden desktop item: ' + ItemPath);
    end;
  end;

  SaveStringToFile(LedgerPath, '', False);
  Log(Format('DeskBox uninstall restored %d desktop items hidden by mapped organize.', [Restored]));
end;
