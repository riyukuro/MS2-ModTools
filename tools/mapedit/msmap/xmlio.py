"""Low-level XML output helpers that mimic .NET XmlWriter (the writer Toolbench/MS2 used).

Two dialects are needed:
  * MS2 .xblock  : UTF-8 BOM, CRLF, tab indent, attributes on one line, no trailing newline.
  * Toolbench .block/.layer/.emtproj : UTF-8 BOM, CRLF, 2-space indent, NewLineOnAttributes.
"""

_ESC = {'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', '\t': '&#x9;', '\n': '&#xA;', '\r': '&#xD;'}


def esc_attr(s):
    """Escape like XmlWriter.WriteAttributeString."""
    out = None
    for ch in s:
        if ch in _ESC or ord(ch) < 0x20:
            break
    else:
        return s
    parts = []
    for ch in s:
        if ch in _ESC:
            parts.append(_ESC[ch])
        elif ord(ch) < 0x20:
            parts.append('&#x%X;' % ord(ch))
        else:
            parts.append(ch)
    return ''.join(parts)


def esc_text(s):
    """Escape like XmlWriter.WriteString in element content."""
    return (s.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
             .replace('\r', '&#xD;'))


class NetWriter:
    """Tiny streaming writer reproducing XmlWriter(Indent=True[, NewLineOnAttributes=True]) output.

    Usage: w.start('a', [('k','v')]); w.text(...) / w.start(...); w.end()
    Elements without content are emitted self-closed (' />'); elements that received text (even
    empty, via text('')) get a full end tag on the same line.
    """
    NL = '\r\n'

    def __init__(self, out, indent='  ', newline_attrs=False):
        self.out = out          # list-like with append(str)
        self.indent = indent
        self.nla = newline_attrs
        self.stack = []         # [name, has_children, has_text, open_tag_pending]
        self.out.append('﻿<?xml version="1.0" encoding="utf-8"?>')

    def _close_pending(self):
        if self.stack and self.stack[-1][3]:
            self.out.append('>')
            self.stack[-1][3] = False

    def start(self, name, attrs=()):
        self._close_pending()
        if self.stack:
            self.stack[-1][1] = True
        depth = len(self.stack)
        buf = [self.NL, self.indent * depth, '<', name]
        if self.nla:
            ai = self.indent * (depth + 1)
            for k, v in attrs:
                buf += [self.NL, ai, k, '="', esc_attr(v), '"']
        else:
            for k, v in attrs:
                buf += [' ', k, '="', esc_attr(v), '"']
        self.out.append(''.join(buf))
        self.stack.append([name, False, False, True])

    def text(self, s):
        self._close_pending()
        self.stack[-1][2] = True
        self.out.append(esc_text(s))

    def end(self):
        name, has_children, has_text, pending = self.stack.pop()
        if pending:
            self.out.append(' />')
        elif has_children and not has_text:
            self.out.append(self.NL + self.indent * len(self.stack) + '</' + name + '>')
        else:
            self.out.append('</' + name + '>')

    def empty(self, name, attrs=()):
        self.start(name, attrs)
        self.end()

    def leaf(self, name, text, attrs=()):
        self.start(name, attrs)
        self.text(text)
        self.end()

    def finish(self):
        assert not self.stack
        return ''.join(self.out)
