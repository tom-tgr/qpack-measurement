"""Just enough RFC 9204 wire format to synthesise the decoder's Insert Count Increment.

pylsqpack does not return Insert Count Increment instructions from Decoder.feed_encoder(), so without
them an encoder limited to acknowledged entries (SETTINGS_QPACK_BLOCKED_STREAMS = 0) never learns that
its inserts arrived. We count the inserts in the encoder stream and send the increment ourselves.
"""


def decode_prefix_int(data, pos, prefix_bits):
    """RFC 7541 5.1 prefixed integer. Returns (value, next_pos)."""
    limit = (1 << prefix_bits) - 1
    value = data[pos] & limit
    pos += 1
    if value < limit:
        return value, pos
    shift = 0
    while True:
        byte = data[pos]
        pos += 1
        value += (byte & 0x7F) << shift
        shift += 7
        if not byte & 0x80:
            return value, pos


def encode_prefix_int(value, prefix_bits, first_byte_flags=0):
    limit = (1 << prefix_bits) - 1
    if value < limit:
        return bytes([first_byte_flags | value])
    out = [first_byte_flags | limit]
    value -= limit
    while value >= 0x80:
        out.append((value & 0x7F) | 0x80)
        value >>= 7
    out.append(value)
    return bytes(out)


def _skip_string(data, pos, prefix_bits):
    length, pos = decode_prefix_int(data, pos, prefix_bits)
    return pos + length


def count_inserts(encoder_stream):
    """Number of table insertions (inserts + duplicates) in a chunk of complete encoder instructions."""
    inserts, pos = 0, 0
    while pos < len(encoder_stream):
        first = encoder_stream[pos]
        if first & 0x80:  # 1Txxxxxx  Insert with Name Reference
            _, pos = decode_prefix_int(encoder_stream, pos, 6)
            pos = _skip_string(encoder_stream, pos, 7)
            inserts += 1
        elif first & 0x40:  # 01Hxxxxx Insert with Literal Name
            pos = _skip_string(encoder_stream, pos, 5)
            pos = _skip_string(encoder_stream, pos, 7)
            inserts += 1
        elif first & 0x20:  # 001xxxxx Set Dynamic Table Capacity
            _, pos = decode_prefix_int(encoder_stream, pos, 5)
        else:  # 000xxxxx Duplicate
            _, pos = decode_prefix_int(encoder_stream, pos, 5)
            inserts += 1
    return inserts


def insert_count_increment(count):
    """Decoder instruction 00xxxxxx (RFC 9204 4.4.3); empty when there is nothing to acknowledge."""
    return encode_prefix_int(count, 6) if count else b""
