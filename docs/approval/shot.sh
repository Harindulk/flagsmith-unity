#!/bin/sh
# Screenshots the approval dialog two seconds after it opens (the largest window owned by System Events).
sleep 2
W=$(osascript -l JavaScript -e "ObjC.import('CoreGraphics'); const l = ObjC.deepUnwrap(ObjC.castRefToObject(\$.CGWindowListCopyWindowInfo(\$.kCGWindowListOptionOnScreenOnly, 0))); const w = l.filter(x => x.kCGWindowOwnerName == 'System Events').sort((a, b) => b.kCGWindowBounds.Width * b.kCGWindowBounds.Height - a.kCGWindowBounds.Width * a.kCGWindowBounds.Height)[0]; w ? w.kCGWindowNumber : ''" 2>/dev/null)
[ -n "$W" ] && screencapture -x -o -l "$W" "$(dirname "$0")/approval-dialog.png"
