import { useMemo, useState } from "react";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TableSortLabel from "@mui/material/TableSortLabel";
import TableContainer from "@mui/material/TableContainer";

export interface Column<T> {
  key: string;
  header: string;
  /** Cell renderer. */
  render: (row: T) => React.ReactNode;
  /** Value used for sorting; omit to make the column non-sortable. */
  sortValue?: (row: T) => string | number;
  numeric?: boolean;
}

export interface DataTableProps<T> {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string | number;
  /** Optional test id on the tbody. */
  bodyTestId?: string;
}

type Order = "asc" | "desc";

/** Generic client-side sortable table with tabular-nums numeric cells. */
export default function DataTable<T>({ columns, rows, rowKey, bodyTestId }: DataTableProps<T>) {
  const [orderBy, setOrderBy] = useState<string | null>(null);
  const [order, setOrder] = useState<Order>("asc");

  const sorted = useMemo(() => {
    const col = columns.find((c) => c.key === orderBy);
    if (!col?.sortValue) return rows;
    const dir = order === "asc" ? 1 : -1;
    return [...rows].sort((a, b) => {
      const av = col.sortValue!(a);
      const bv = col.sortValue!(b);
      if (av < bv) return -1 * dir;
      if (av > bv) return 1 * dir;
      return 0;
    });
  }, [rows, columns, orderBy, order]);

  const handleSort = (key: string) => {
    if (orderBy === key) {
      setOrder((o) => (o === "asc" ? "desc" : "asc"));
    } else {
      setOrderBy(key);
      setOrder("asc");
    }
  };

  return (
    <TableContainer>
      <Table size="small" sx={{ "& td, & th": { fontVariantNumeric: "tabular-nums" } }}>
        <TableHead>
          <TableRow>
            {columns.map((c) => (
              <TableCell key={c.key} align={c.numeric ? "right" : "left"}>
                {c.sortValue ? (
                  <TableSortLabel
                    active={orderBy === c.key}
                    direction={orderBy === c.key ? order : "asc"}
                    onClick={() => handleSort(c.key)}
                  >
                    {c.header}
                  </TableSortLabel>
                ) : (
                  c.header
                )}
              </TableCell>
            ))}
          </TableRow>
        </TableHead>
        <TableBody data-testid={bodyTestId}>
          {sorted.map((row) => (
            <TableRow key={rowKey(row)} hover>
              {columns.map((c) => (
                <TableCell key={c.key} align={c.numeric ? "right" : "left"}>
                  {c.render(row)}
                </TableCell>
              ))}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}
