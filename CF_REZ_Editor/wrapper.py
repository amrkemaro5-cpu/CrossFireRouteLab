import cf_rez_original as _original
import cf_rez_learned as _learned


def _show_selected_info(self):
    """Extend the existing Resource Information dialog without changing the UI."""
    if not getattr(self, 'selected', None):
        return
    e = self.selected
    try:
        with open(self.arc_path, 'rb') as fh:
            fh.seek(e.data_offset)
            raw = fh.read(e.size)
        report = _learned.format_report(e.path, raw, getattr(_original, 'core', None))
        text = (
            f"Path: {e.path}\n"
            f"Name: {e.name}\n"
            f"Extension: {e.extension}\n"
            f"Size: {e.size:,} bytes\n"
            f"Offset: 0x{e.data_offset:X}\n"
            f"MD5: {e.md5}\n\n"
            "FORMAT ANALYSIS\n"
            + report
        )
    except Exception as exc:
        text = (
            f"Path: {e.path}\n"
            f"Name: {e.name}\n"
            f"Extension: {e.extension}\n"
            f"Size: {e.size:,} bytes\n"
            f"Offset: 0x{e.data_offset:X}\n"
            f"MD5: {e.md5}\n\n"
            f"Format analysis unavailable: {exc}"
        )
    _original.messagebox.showinfo('Resource Information', text)


_original.App.show_selected_info = _show_selected_info

if __name__ == '__main__':
    # Critical startup fix: explicitly create the root App before entering Tk's loop.
    _original.App().mainloop()
