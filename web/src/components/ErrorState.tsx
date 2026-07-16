import Alert from "@mui/material/Alert";

export interface ErrorStateProps {
  message: string;
}

/** Inline error banner for failed fetches. */
export default function ErrorState({ message }: ErrorStateProps) {
  return (
    <Alert severity="error" role="alert" sx={{ my: 1 }}>
      {message}
    </Alert>
  );
}
